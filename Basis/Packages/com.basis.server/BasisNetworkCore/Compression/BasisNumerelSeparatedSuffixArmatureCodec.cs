using System;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Power-2 fixed16 Quaternion-4 Numerel rotations with the non-armature payload carried by
    /// <see cref="BasisAvatarAuxiliaryDeltaCodec"/> as an independent exact baseline-relative suffix.
    ///
    /// Wire body:
    ///   [rotationBytes:u16-le]
    ///   [Power-2 fixed16 Quaternion-4 Numerel rotation stream]
    ///   [exact baseline-relative auxiliary body]
    ///
    /// The rotation stream can optionally use the Gray-preserving zero-bone RLE used by the full
    /// hybrid codec. There is deliberately no passive absolute-refresh side data in this codec; it
    /// represents the historical "Power 2 + separated suffix" benchmark candidate directly.
    /// </summary>
    public static class BasisNumerelSeparatedSuffixArmatureCodec
    {
        public readonly struct Options
        {
            public readonly bool ZeroBoneRleRetainGray;

            public Options(bool zeroBoneRleRetainGray)
            {
                ZeroBoneRleRetainGray = zeroBoneRleRetainGray;
            }

            /// <summary>Historical Power-2 + separated-suffix control, without zero-bone RLE.</summary>
            public static Options Power2 => new Options(false);

            /// <summary>Power-2 + separated suffix + Gray-preserving zero-bone RLE.</summary>
            public static Options Power2RleGray => new Options(true);
        }

        private static BasisNumerelQuaternion4ArmatureCodec.Options RotationOptions(Options options)
            => options.ZeroBoneRleRetainGray
                ? BasisNumerelQuaternion4ArmatureCodec.Options.Power2RleGrayContinuous16Bit
                : BasisNumerelQuaternion4ArmatureCodec.Options.Power2Continuous16Bit;

        public sealed class Encoder
        {
            private readonly BasisNumerelQuaternion4ArmatureCodec.Encoder _rotations;
            private readonly BasisAvatarAuxiliaryDeltaCodec.Encoder _auxiliary;
            private readonly byte[] _auxiliaryScratch;
            private readonly int _maxBodySize;
            private bool _auxiliaryBootstrapPending;

            public Encoder(BasisAvatarBitPacking.BitQuality quality)
                : this(quality, Options.Power2RleGray)
            {
            }

            public Encoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _rotations = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, RotationOptions(options));
                _auxiliary = new BasisAvatarAuxiliaryDeltaCodec.Encoder(quality);
                if (_rotations.PayloadSize != _auxiliary.PayloadSize)
                    throw new InvalidOperationException("Separated-suffix rotation and auxiliary payload sizes differ.");
                if (_rotations.MaxRotationBodySize > ushort.MaxValue)
                    throw new InvalidOperationException("Separated-suffix rotation body does not fit its u16 length field.");
                _auxiliaryScratch = new byte[_auxiliary.MaxBodySize];
                _maxBodySize = 2 + _rotations.MaxRotationBodySize + _auxiliary.MaxBodySize;
                Reset();
            }

            public int PayloadSize => _rotations.PayloadSize;
            public int MaxBodySize => _maxBodySize;
            public int LastRotationBytes { get; private set; }
            public int LastRotationBits => _rotations.LastArmatureBits;
            public int LastAuxiliaryBytes { get; private set; }
            public bool LastWasAuxiliaryBootstrap { get; private set; }
            public bool LastUsedZeroBoneRle => _rotations.LastUsedZeroBoneRle;
            public int LastZeroBoneCount => _rotations.LastZeroBoneCount;
            public int LastZeroRunCount => _rotations.LastZeroRunCount;
            public int LastZeroBoneRleMetadataBits => _rotations.LastZeroBoneRleMetadataBits;
            public int LastZeroBoneRleNetSavedBits => _rotations.LastZeroBoneRleNetSavedBits;

            public void Reset()
            {
                _rotations.Reset();
                _auxiliary.Reset();
                // The auxiliary codec automatically bootstraps when it has no baseline. This flag
                // is only for an explicit rebootstrap request after the baseline already exists.
                _auxiliaryBootstrapPending = false;
                LastRotationBytes = 0;
                LastAuxiliaryBytes = 0;
                LastWasAuxiliaryBootstrap = false;
            }

            /// <summary>
            /// Forces the exact auxiliary baseline to be resent on the next packet. This does not
            /// reset or bootstrap the stateful Numerel rotation predictor.
            /// </summary>
            public void RequestAuxiliaryBootstrap() => _auxiliaryBootstrapPending = true;

            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
            {
                if (payload == null || payload.Length < PayloadSize || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                // Encode the independent suffix first. If the stateful rotation encode then fails,
                // force the next successful suffix to bootstrap so a receiver can never miss an
                // auxiliary baseline transition caused by this failed outer packet.
                int auxiliaryLength = _auxiliary.Encode(
                    payload, _auxiliaryBootstrapPending, _auxiliaryScratch, 0);
                if (auxiliaryLength <= 0) return -1;
                bool auxiliaryWasBootstrap = _auxiliary.LastWasBootstrap;

                int rotationStart = destinationStart + 2;
                int rotationLength = _rotations.EncodeRotations(payload, sequence, destination, rotationStart);
                if (rotationLength <= 0)
                {
                    _auxiliaryBootstrapPending = true;
                    return -1;
                }

                destination[destinationStart] = (byte)rotationLength;
                destination[destinationStart + 1] = (byte)(rotationLength >> 8);

                int auxiliaryStart = rotationStart + rotationLength;
                Buffer.BlockCopy(_auxiliaryScratch, 0, destination, auxiliaryStart, auxiliaryLength);

                _auxiliaryBootstrapPending = false;
                LastRotationBytes = rotationLength;
                LastAuxiliaryBytes = auxiliaryLength;
                LastWasAuxiliaryBootstrap = auxiliaryWasBootstrap;
                return 2 + rotationLength + auxiliaryLength;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly BasisNumerelQuaternion4ArmatureCodec.Decoder _rotations;
            private readonly BasisAvatarAuxiliaryDeltaCodec.Decoder _auxiliary;
            private readonly int _maxBodySize;

            public Decoder(BasisAvatarBitPacking.BitQuality quality)
                : this(quality, Options.Power2RleGray)
            {
            }

            public Decoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                BasisNumerelQuaternion4ArmatureCodec.Options rotationOptions = RotationOptions(options);
                _rotations = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, rotationOptions);
                _auxiliary = new BasisAvatarAuxiliaryDeltaCodec.Decoder(quality);
                if (_rotations.PayloadSize != BasisAvatarAuxiliaryDeltaCodec.PayloadSize(quality))
                    throw new InvalidOperationException("Separated-suffix rotation and auxiliary payload sizes differ.");
                _maxBodySize = 2
                    + BasisNumerelQuaternion4ArmatureCodec.GetMaxRotationBodySize(quality, rotationOptions)
                    + BasisAvatarAuxiliaryDeltaCodec.MaxBodySize(quality);
                Reset();
            }

            public int PayloadSize => _rotations.PayloadSize;
            public int MaxBodySize => _maxBodySize;
            public bool HasSequence => _rotations.HasSequence;
            public byte LastSequence => _rotations.LastSequence;
            public int DeadlinePredictionsSinceDecode => _rotations.DeadlinePredictionsSinceDecode;
            public int LastRotationBytes { get; private set; }
            public int LastAuxiliaryBytes { get; private set; }
            public bool LastWasAuxiliaryBootstrap { get; private set; }

            public void Reset()
            {
                _rotations.Reset();
                _auxiliary.Reset();
                LastRotationBytes = 0;
                LastAuxiliaryBytes = 0;
                LastWasAuxiliaryBootstrap = false;
            }

            public void CopyDisplayedPose(byte[] destination) => _rotations.CopyDisplayedPose(destination);

            /// <summary>
            /// Advances the rotation predictor for one missing expected frame. Exact auxiliary data
            /// remains held at the last successfully decoded value.
            /// </summary>
            public bool TryAdvanceDeadline(byte expectedSequence, byte[] outputPayload)
                => _rotations.TryAdvanceDeadline(expectedSequence, outputPayload);

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
                if (sourceStart < 0 || availableBytes < 3 || sourceStart + availableBytes > source.Length) return false;

                int rotationLength = source[sourceStart] | (source[sourceStart + 1] << 8);
                if (rotationLength <= 0) return false;

                int rotationStart = sourceStart + 2;
                int auxiliaryStart = rotationStart + rotationLength;
                int packetEnd = sourceStart + availableBytes;
                if (auxiliaryStart >= packetEnd) return false;

                int auxiliaryAvailable = packetEnd - auxiliaryStart;
                if (!BasisAvatarAuxiliaryDeltaCodec.TryGetBodyLength(
                    source, auxiliaryStart, auxiliaryAvailable, _quality, out int auxiliaryLength, out bool auxiliaryBootstrap))
                    return false;
                if (auxiliaryLength != auxiliaryAvailable) return false;
                if (!_auxiliary.CanDecode(
                    source, auxiliaryStart, auxiliaryLength, out int checkedAuxiliaryLength, out bool checkedBootstrap)
                    || checkedAuxiliaryLength != auxiliaryLength
                    || checkedBootstrap != auxiliaryBootstrap)
                    return false;

                // Auxiliary parsing is completely validated before the stateful Numerel rotation
                // decode commits, so malformed/truncated suffix data cannot advance the predictor.
                if (!_rotations.TryDecodeRotations(
                    source, rotationStart, rotationLength, sequence, outputPayload, out int consumedRotation)
                    || consumedRotation != rotationLength)
                    return false;

                // CanDecode above validates every parse/baseline precondition before the stateful
                // rotation commit. Applying the suffix now has no remaining failure path.
                _auxiliary.DecodeValidated(source, auxiliaryStart, outputPayload, auxiliaryBootstrap);

                _rotations.SyncAuxiliaryFromPayload(outputPayload);
                LastRotationBytes = rotationLength;
                LastAuxiliaryBytes = auxiliaryLength;
                LastWasAuxiliaryBootstrap = auxiliaryBootstrap;
                consumedBytes = availableBytes;
                return true;
            }
        }
    }
}
