using System;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Experimental Power-2 Numerel hybrid used by the armature benchmark.
    ///
    /// Wire body:
    ///   [rotationBytes:u16-le]
    ///   [Power2 fixed16 Quaternion-4 rotation stream, optionally zero-bone RLE]
    ///   [exact baseline-relative auxiliary body]
    ///   [zero or more absolute rotation-group refresh records]
    ///
    /// The normal shared rotation stream remains one Numerel predictor for every Quaternion-4
    /// scalar. Eight balanced passive recovery groups periodically append exact Basis rotations and
    /// rebase only those sender/receiver scalar states. Any missed normal rotation frame invalidates
    /// every group's recovery-validity bit because all shared predictors advanced on the sender.
    /// Scheduled absolute groups then restore validity independently in the background.
    /// </summary>
    public static class BasisNumerelHybridArmatureCodec
    {
        public const byte RecoveryGroupCount = 8;
        public const byte AllRecoveryGroupsMask = 0xFF;
        private static readonly byte[] BoneGroups = BuildBalancedBoneGroups();

        public readonly struct Options
        {
            public readonly byte RefreshCycleFrames;
            public readonly bool BootstrapOnReset;

            public Options(byte refreshCycleFrames, bool bootstrapOnReset = true)
            {
                if (refreshCycleFrames < RecoveryGroupCount || refreshCycleFrames > 32)
                    throw new ArgumentOutOfRangeException(nameof(refreshCycleFrames));
                RefreshCycleFrames = refreshCycleFrames;
                BootstrapOnReset = bootstrapOnReset;
            }

            public static Options PassiveG8C12 => new Options(12, true);
        }

        public static byte GetBoneRecoveryGroup(int boneSlot)
        {
            if ((uint)boneSlot >= BasisBoneRotationCompression.SyncBoneCount)
                throw new ArgumentOutOfRangeException(nameof(boneSlot));
            return BoneGroups[boneSlot];
        }

        public static byte ScheduledRefreshMask(byte sequence, Options options)
        {
            var schedule = new BasisAvatarDeltaRecoveryV3.Options(options.RefreshCycleFrames, 0, false);
            return BasisAvatarDeltaRecoveryV3.ScheduledRefreshMask(sequence, schedule);
        }

        public sealed class Encoder
        {
            private readonly Options _options;
            private readonly BasisNumerelQuaternion4ArmatureCodec.Encoder _rotations;
            private readonly BasisAvatarAuxiliaryDeltaCodec.Encoder _auxiliary;
            private readonly int _maxBodySize;
            private bool _bootstrapPending;

            public Encoder(BasisAvatarBitPacking.BitQuality quality)
                : this(quality, Options.PassiveG8C12)
            {
            }

            public Encoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _options = options;
                _rotations = new BasisNumerelQuaternion4ArmatureCodec.Encoder(
                    quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit);
                _auxiliary = new BasisAvatarAuxiliaryDeltaCodec.Encoder(quality);

                int bootstrapRefreshBytes = 0;
                for (byte group = 0; group < RecoveryGroupCount; group++)
                {
                    int size = _rotations.GetAbsoluteGroupRefreshSize(BoneGroups, group, RecoveryGroupCount);
                    if (size <= 0) throw new InvalidOperationException("Invalid hybrid recovery-group mapping.");
                    bootstrapRefreshBytes += size;
                }
                _maxBodySize = 2 + _rotations.MaxRotationBodySize + _auxiliary.MaxBodySize + bootstrapRefreshBytes;
                Reset();
            }

            public int PayloadSize => _rotations.PayloadSize;
            public int MaxBodySize => _maxBodySize;
            public int LastRotationBytes { get; private set; }
            public int LastRotationBits => _rotations.LastArmatureBits;
            public int LastAuxiliaryBytes { get; private set; }
            public int LastRefreshBytes { get; private set; }
            public byte LastRefreshMask { get; private set; }
            public bool LastWasBootstrap { get; private set; }
            public bool LastUsedZeroBoneRle => _rotations.LastUsedZeroBoneRle;
            public int LastZeroBoneCount => _rotations.LastZeroBoneCount;
            public int LastZeroRunCount => _rotations.LastZeroRunCount;
            public int LastZeroBoneRleMetadataBits => _rotations.LastZeroBoneRleMetadataBits;
            public int LastZeroBoneRleNetSavedBits => _rotations.LastZeroBoneRleNetSavedBits;

            public void Reset()
            {
                _rotations.Reset();
                _auxiliary.Reset();
                _bootstrapPending = _options.BootstrapOnReset;
                LastRotationBytes = 0;
                LastAuxiliaryBytes = 0;
                LastRefreshBytes = 0;
                LastRefreshMask = 0;
                LastWasBootstrap = false;
            }

            /// <summary>For late join or an explicit recovery boundary, bootstrap on the next packet.</summary>
            public void RequestBootstrap() => _bootstrapPending = true;

            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
            {
                if (payload == null || payload.Length < PayloadSize || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                bool bootstrap = _bootstrapPending;
                int rotationStart = destinationStart + 2;
                int rotationLength = _rotations.EncodeRotations(payload, sequence, destination, rotationStart);
                if (rotationLength <= 0 || rotationLength > ushort.MaxValue) return -1;
                destination[destinationStart] = (byte)rotationLength;
                destination[destinationStart + 1] = (byte)(rotationLength >> 8);

                int o = rotationStart + rotationLength;
                int auxiliaryLength = _auxiliary.Encode(payload, bootstrap, destination, o);
                if (auxiliaryLength <= 0) return -1;
                o += auxiliaryLength;

                byte refreshMask = bootstrap ? AllRecoveryGroupsMask : ScheduledRefreshMask(sequence, _options);
                int refreshBytes = 0;
                for (byte group = 0; group < RecoveryGroupCount; group++)
                {
                    byte bit = (byte)(1 << group);
                    if ((refreshMask & bit) == 0) continue;
                    int written = _rotations.EncodeAbsoluteGroupRefresh(
                        payload, BoneGroups, group, RecoveryGroupCount, destination, o);
                    if (written <= 0) return -1;
                    o += written;
                    refreshBytes += written;
                }

                _bootstrapPending = false;
                LastRotationBytes = rotationLength;
                LastAuxiliaryBytes = auxiliaryLength;
                LastRefreshBytes = refreshBytes;
                LastRefreshMask = refreshMask;
                LastWasBootstrap = bootstrap;
                return o - destinationStart;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly BasisNumerelQuaternion4ArmatureCodec.Decoder _rotations;
            private readonly BasisAvatarAuxiliaryDeltaCodec.Decoder _auxiliary;
            private readonly byte[] _rollbackPayload;
            private readonly int _maxBodySize;
            private bool _hasSequence;
            private byte _lastSequence;
            private byte _validGroupMask;

            public Decoder(BasisAvatarBitPacking.BitQuality quality)
                : this(quality, Options.PassiveG8C12)
            {
            }

            public Decoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _rotations = new BasisNumerelQuaternion4ArmatureCodec.Decoder(
                    quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit);
                _auxiliary = new BasisAvatarAuxiliaryDeltaCodec.Decoder(quality);
                _rollbackPayload = new byte[_rotations.PayloadSize];

                int bootstrapRefreshBytes = 0;
                for (byte group = 0; group < RecoveryGroupCount; group++)
                {
                    int size = _rotations.GetAbsoluteGroupRefreshSize(BoneGroups, group, RecoveryGroupCount);
                    if (size <= 0) throw new InvalidOperationException("Invalid hybrid recovery-group mapping.");
                    bootstrapRefreshBytes += size;
                }
                _maxBodySize = 2
                    + BasisNumerelQuaternion4ArmatureCodec.GetMaxRotationBodySize(
                        quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit)
                    + BasisAvatarAuxiliaryDeltaCodec.MaxBodySize(quality)
                    + bootstrapRefreshBytes;
                Reset();
            }

            public int PayloadSize => _rotations.PayloadSize;
            public int MaxBodySize => _maxBodySize;
            public bool HasSequence => _hasSequence;
            public byte LastSequence => _lastSequence;
            public byte ValidGroupMask => _validGroupMask;
            public byte MissingGroupMask => (byte)(AllRecoveryGroupsMask & ~_validGroupMask);
            public bool IsFullySynchronized => _validGroupMask == AllRecoveryGroupsMask;
            public int DeadlinePredictionsSinceDecode => _rotations.DeadlinePredictionsSinceDecode;
            public int LastRotationBytes { get; private set; }
            public int LastAuxiliaryBytes { get; private set; }
            public int LastRefreshBytes { get; private set; }
            public byte LastRefreshMask { get; private set; }
            public bool LastWasBootstrap { get; private set; }

            public void Reset()
            {
                _rotations.Reset();
                _auxiliary.Reset();
                _hasSequence = false;
                _lastSequence = 0;
                _validGroupMask = 0;
                LastRotationBytes = 0;
                LastAuxiliaryBytes = 0;
                LastRefreshBytes = 0;
                LastRefreshMask = 0;
                LastWasBootstrap = false;
                Array.Clear(_rollbackPayload, 0, _rollbackPayload.Length);
            }

            public void CopyDisplayedPose(byte[] destination) => _rotations.CopyDisplayedPose(destination);

            /// <summary>
            /// Applies immediate deadline feed-forward for a missing expected frame. Since every
            /// shared Numerel scalar would have advanced on the sender, a missed normal frame makes
            /// every passive recovery group potentially divergent until refreshed again.
            /// Auxiliary state is held exactly at its last received value.
            /// </summary>
            public bool TryAdvanceDeadline(byte expectedSequence, byte[] outputPayload)
            {
                if (!_hasSequence || expectedSequence != unchecked((byte)(_lastSequence + 1))) return false;
                if (!_rotations.TryAdvanceDeadline(expectedSequence, outputPayload)) return false;
                _lastSequence = expectedSequence;
                _validGroupMask = 0;
                return true;
            }

            public bool TryDecode(
                byte[] source,
                int sourceStart,
                int availableBytes,
                byte sequence,
                byte[] outputPayload,
                out int consumedBytes)
            {
                consumedBytes = 0;
                if (source == null || outputPayload == null || outputPayload.Length < PayloadSize) return false;
                if (sourceStart < 0 || availableBytes < 2 || sourceStart + availableBytes > source.Length) return false;

                int forward = 1;
                if (_hasSequence)
                {
                    forward = (byte)(sequence - _lastSequence);
                    if (forward == 0 || forward >= 128) return false;
                }

                int rotationLength = source[sourceStart] | (source[sourceStart + 1] << 8);
                if (rotationLength <= 0) return false;
                int rotationStart = sourceStart + 2;
                int auxiliaryStart = rotationStart + rotationLength;
                int packetEnd = sourceStart + availableBytes;
                if (auxiliaryStart >= packetEnd) return false;

                int afterRotation = availableBytes - 2 - rotationLength;
                if (afterRotation <= 0) return false;
                if (!BasisAvatarAuxiliaryDeltaCodec.TryGetBodyLength(
                    source, auxiliaryStart, afterRotation, _quality, out int auxiliaryLength, out bool bootstrap))
                    return false;
                if (!_auxiliary.CanDecode(source, auxiliaryStart, auxiliaryLength, out int checkedAuxiliaryLength, out bool checkedBootstrap)
                    || checkedAuxiliaryLength != auxiliaryLength || checkedBootstrap != bootstrap)
                    return false;

                byte expectedRefreshMask = bootstrap ? AllRecoveryGroupsMask : ScheduledRefreshMask(sequence, _options);
                int refreshStart = auxiliaryStart + auxiliaryLength;
                int validateOffset = refreshStart;
                int refreshBytes = 0;
                for (byte group = 0; group < RecoveryGroupCount; group++)
                {
                    byte bit = (byte)(1 << group);
                    if ((expectedRefreshMask & bit) == 0) continue;
                    int remaining = packetEnd - validateOffset;
                    if (!_rotations.TryValidateAbsoluteGroupRefresh(
                        source, validateOffset, remaining, BoneGroups, group, RecoveryGroupCount, out int recordLength))
                        return false;
                    validateOffset += recordLength;
                    refreshBytes += recordLength;
                }
                if (validateOffset != packetEnd) return false;

                byte nextValid = _validGroupMask;
                if (bootstrap)
                {
                    nextValid = 0;
                }
                else if (_hasSequence && forward > 1)
                {
                    // Missing any normal Numerel frame potentially diverges all four-component
                    // predictors, not merely the group that happened to refresh on that frame.
                    nextValid = 0;
                }

                _rotations.CopyDisplayedPose(_rollbackPayload);
                if (!_rotations.TryDecodeRotations(
                    source, rotationStart, rotationLength, sequence, outputPayload, out int consumedRotation)
                    || consumedRotation != rotationLength)
                    return false;

                if (!_auxiliary.TryDecode(
                    source, auxiliaryStart, auxiliaryLength, outputPayload, out int consumedAuxiliary)
                    || consumedAuxiliary != auxiliaryLength)
                {
                    AbortAfterPartialCommit(outputPayload);
                    return false;
                }
                _rotations.SyncAuxiliaryFromPayload(outputPayload);

                int applyOffset = refreshStart;
                for (byte group = 0; group < RecoveryGroupCount; group++)
                {
                    byte bit = (byte)(1 << group);
                    if ((expectedRefreshMask & bit) == 0) continue;
                    int recordLength = _rotations.GetAbsoluteGroupRefreshSize(BoneGroups, group, RecoveryGroupCount);
                    if (recordLength <= 0
                        || !_rotations.TryApplyAbsoluteGroupRefresh(
                            source, applyOffset, recordLength, BoneGroups, group, RecoveryGroupCount, outputPayload))
                    {
                        AbortAfterPartialCommit(outputPayload);
                        return false;
                    }
                    applyOffset += recordLength;
                    nextValid |= bit;
                }

                _hasSequence = true;
                _lastSequence = sequence;
                _validGroupMask = nextValid;
                LastRotationBytes = rotationLength;
                LastAuxiliaryBytes = auxiliaryLength;
                LastRefreshBytes = refreshBytes;
                LastRefreshMask = expectedRefreshMask;
                LastWasBootstrap = bootstrap;
                consumedBytes = availableBytes;
                return true;
            }

            private void AbortAfterPartialCommit(byte[] outputPayload)
            {
                _rotations.ResetForBootstrapKeepingDisplayedPose(_rollbackPayload);
                _auxiliary.Reset();
                _hasSequence = false;
                _lastSequence = 0;
                _validGroupMask = 0;
                LastRotationBytes = 0;
                LastAuxiliaryBytes = 0;
                LastRefreshBytes = 0;
                LastRefreshMask = 0;
                LastWasBootstrap = false;
                Buffer.BlockCopy(_rollbackPayload, 0, outputPayload, 0, _rollbackPayload.Length);
            }
        }

        private static byte[] BuildBalancedBoneGroups()
        {
            byte[] bpc = BasisBoneRotationCompression.BPC_HIGH;
            var weights = new int[bpc.Length];
            var assigned = new bool[bpc.Length];
            var loads = new int[RecoveryGroupCount];
            var result = new byte[bpc.Length];

            for (int bone = 0; bone < bpc.Length; bone++)
                weights[bone] = 1 + 2 + 3 * bpc[bone]; // q/-q sign plus exact smallest-three bits

            for (int n = 0; n < bpc.Length; n++)
            {
                int bone = -1;
                int weight = -1;
                for (int candidate = 0; candidate < bpc.Length; candidate++)
                {
                    if (assigned[candidate]) continue;
                    if (weights[candidate] > weight)
                    {
                        bone = candidate;
                        weight = weights[candidate];
                    }
                }

                int group = 0;
                for (int candidate = 1; candidate < RecoveryGroupCount; candidate++)
                    if (loads[candidate] < loads[group]) group = candidate;
                result[bone] = checked((byte)group);
                loads[group] += weight;
                assigned[bone] = true;
            }
            return result;
        }
    }
}
