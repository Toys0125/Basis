using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Experimental keyframe-free armature stream built on <see cref="BasisNumerel"/>.
    ///
    /// Each of the 51 existing smallest-three bone values is split into an exact omitted
    /// quaternion-component index plus three temporally coded quantized components at the
    /// quality's existing BPC. Pure modes use Numerel for every component; Hybrid V2 uses
    /// exact small temporal deltas for coarse fingers/toes and Numerel for higher-BPC bones.
    ///
    /// Every bone is present in every frame. Rotating per-bone absolutes provide bounded
    /// resynchronization without requiring a whole-armature keyframe. Position, scale,
    /// hips and High-quality end-effector anchors remain absolute per frame in this POC so
    /// the experiment isolates armature behavior and cannot accumulate world-position drift.
    ///
    /// Body layout (the network frame carries quality and sequence separately):
    ///   [51 variable bone records, MSB-first][zero pad to byte]
    ///   [absolute position bytes][absolute tail/end-effector bytes]
    /// </summary>
    public static class BasisNumerelArmatureCodec
    {
        private const int ComponentsPerBone = 3;
        private const int StateCount = BasisBoneRotationCompression.SyncBoneCount * ComponentsPerBone;

        public readonly struct Options
        {
            public readonly BasisNumerel.Tuning Numerel;
            public readonly bool BoneAbsoluteEscape;
            public readonly ushort ResidualDivisor;
            public readonly byte RefreshBonesPerFrame;
            public readonly byte ExactDeltaMaxBits;
            public readonly byte MaxPredictionAge;
            public readonly bool RefreshNumerelBonesOnly;

            public Options(
                BasisNumerel.Tuning numerel,
                bool boneAbsoluteEscape,
                ushort residualDivisor = 512,
                byte refreshBonesPerFrame = 0,
                byte exactDeltaMaxBits = 0,
                byte maxPredictionAge = 0,
                bool refreshNumerelBonesOnly = false)
            {
                if (boneAbsoluteEscape && residualDivisor < 2) throw new ArgumentOutOfRangeException(nameof(residualDivisor));
                if (refreshBonesPerFrame > BasisBoneRotationCompression.SyncBoneCount) throw new ArgumentOutOfRangeException(nameof(refreshBonesPerFrame));
                if (exactDeltaMaxBits > 30) throw new ArgumentOutOfRangeException(nameof(exactDeltaMaxBits));
                Numerel = numerel;
                BoneAbsoluteEscape = boneAbsoluteEscape;
                ResidualDivisor = residualDivisor;
                RefreshBonesPerFrame = refreshBonesPerFrame;
                ExactDeltaMaxBits = exactDeltaMaxBits;
                MaxPredictionAge = maxPredictionAge;
                RefreshNumerelBonesOnly = refreshNumerelBonesOnly;
            }

            public static Options NumerelOnly(BasisNumerel.Tuning tuning) => new Options(tuning, false, 2, 0);
            public static Options HybridPoc => new Options(new BasisNumerel.Tuning(1, -1, true, false), true, 512, 4);

            /// <summary>
            /// Second-generation Basis experiment derived from the real Humanoid clip matrix:
            /// low-BPC fingers/toes use exact temporal deltas with rotating absolute recovery,
            /// while high-BPC body bones use nearest-cube Numerel with a less aggressive residual
            /// escape. Receivers keep bounded per-bone prediction confidence
            /// across ordinary packet gaps instead of freezing the entire skeleton.
            /// </summary>
            public static Options HybridV2 => new Options(
                new BasisNumerel.Tuning(1, -1, true, false),
                boneAbsoluteEscape: true,
                residualDivisor: 256,
                refreshBonesPerFrame: 12,
                exactDeltaMaxBits: 6,
                maxPredictionAge: 8,
                refreshNumerelBonesOnly: false);
        }

        public sealed class Encoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly BasisNumerel.Tuning _tuning;
            private readonly Options _options;
            private readonly byte[] _bpc;
            private BasisNumerel.TxState[] _states;
            private BasisNumerel.TxState[] _scratchStates;
            private readonly int _positionBytes;
            private readonly int _rotationBytes;
            private readonly int _tailOffset;
            private readonly int _tailBytes;
            private readonly int _payloadBytes;

            public Encoder(BasisAvatarBitPacking.BitQuality quality, BasisNumerel.Tuning tuning)
                : this(quality, Options.NumerelOnly(tuning))
            {
            }

            public Encoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _tuning = options.Numerel;
                _bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                _positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                _rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                _tailOffset = _positionBytes + _rotationBytes;
                _payloadBytes = BasisBoneRotationCompression.ConvertToSize(quality);
                _tailBytes = _payloadBytes - _tailOffset;
                _states = new BasisNumerel.TxState[StateCount];
                _scratchStates = new BasisNumerel.TxState[StateCount];
                Reset();
            }

            public BasisAvatarBitPacking.BitQuality Quality => _quality;
            public int PayloadSize => _payloadBytes;
            public int MaxBodySize => GetMaxBodySize(_quality, _options);
            public int LastArmatureBits { get; private set; }
            public int AbsoluteBonesLastFrame { get; private set; }

            /// <summary>
            /// Both ends start quantized components at their neutral midpoint. This is the
            /// natural value for zero quaternion components and substantially shortens cold join.
            /// </summary>
            public void Reset()
            {
                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    uint midpoint = 1u << (_bpc[bone] - 1);
                    for (int c = 0; c < ComponentsPerBone; c++)
                    {
                        _states[state].Reset(midpoint);
                        _scratchStates[state].Reset(midpoint);
                        state++;
                    }
                }
                LastArmatureBits = 0;
                AbsoluteBonesLastFrame = 0;
            }

            /// <summary>
            /// Encodes one full fixed-size Basis avatar payload. State commits only when the
            /// complete body fits and validates; a failed encode cannot poison following frames.
            /// </summary>
            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
            {
                if (payload == null || payload.Length < _payloadBytes || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                Array.Copy(_states, _scratchStates, StateCount);
                Array.Clear(destination, destinationStart, MaxBodySize);

                int bitPosition = destinationStart * 8;
                int bitLimit = (destinationStart + MaxBodySize) * 8;
                int sourceBit = _positionBytes * 8;
                int stateIndex = 0;
                int absoluteBones = 0;

                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int componentBits = _bpc[bone];
                    int packedWidth = 2 + componentBits * 3;
                    ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref sourceBit, packedWidth);
                    uint mask = (1u << componentBits) - 1u;
                    uint largest = (uint)(packed & 3u);
                    uint a = (uint)((packed >> 2) & mask);
                    uint b = (uint)((packed >> (2 + componentBits)) & mask);
                    uint c = (uint)((packed >> (2 + componentBits * 2)) & mask);
                    int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                    bool exactDelta = UsesExactDeltaPath(componentBits, _options);
                    bool absolute = IsScheduledRefreshBone(bone, sequence, _bpc, _options);
                    if (exactDelta)
                    {
                        int exactDeltaBits = ExactDeltaBitCount(_scratchStates[stateIndex].RemoteEstimate, a, componentBits)
                            + ExactDeltaBitCount(_scratchStates[stateIndex + 1].RemoteEstimate, b, componentBits)
                            + ExactDeltaBitCount(_scratchStates[stateIndex + 2].RemoteEstimate, c, componentBits);
                        absolute |= exactDeltaBits >= ComponentsPerBone * componentBits;
                    }
                    else if (_options.BoneAbsoluteEscape)
                    {
                        int threshold = Math.Max(1, (int)((mask + 1u) / _options.ResidualDivisor));
                        uint predictedA = BasisNumerel.PredictRemoteEstimate(_scratchStates[stateIndex].RemoteEstimate, a, grayBit, componentBits, false, _tuning);
                        uint predictedB = BasisNumerel.PredictRemoteEstimate(_scratchStates[stateIndex + 1].RemoteEstimate, b, grayBit, componentBits, false, _tuning);
                        uint predictedC = BasisNumerel.PredictRemoteEstimate(_scratchStates[stateIndex + 2].RemoteEstimate, c, grayBit, componentBits, false, _tuning);
                        absolute |= Math.Abs((int)a - (int)predictedA) > threshold
                            || Math.Abs((int)b - (int)predictedB) > threshold
                            || Math.Abs((int)c - (int)predictedC) > threshold;
                    }
                    if (UsesPerBoneMode(_options))
                    {
                        if (!TryWriteRawBits(destination, ref bitPosition, bitLimit, absolute ? 1u : 0u, 1)) return -1;
                    }

                    if (!TryWriteRawBits(destination, ref bitPosition, bitLimit, largest, 2)) return -1;
                    if (absolute)
                    {
                        if (!TryWriteRawBits(destination, ref bitPosition, bitLimit, a, componentBits)
                            || !TryWriteRawBits(destination, ref bitPosition, bitLimit, b, componentBits)
                            || !TryWriteRawBits(destination, ref bitPosition, bitLimit, c, componentBits)) return -1;
                        _scratchStates[stateIndex++].Reset(a);
                        _scratchStates[stateIndex++].Reset(b);
                        _scratchStates[stateIndex++].Reset(c);
                        absoluteBones++;
                    }
                    else if (exactDelta)
                    {
                        if (!TryWriteExactDelta(ref _scratchStates[stateIndex++], a, componentBits, destination, ref bitPosition, bitLimit)
                            || !TryWriteExactDelta(ref _scratchStates[stateIndex++], b, componentBits, destination, ref bitPosition, bitLimit)
                            || !TryWriteExactDelta(ref _scratchStates[stateIndex++], c, componentBits, destination, ref bitPosition, bitLimit)) return -1;
                    }
                    else
                    {
                        if (!BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], a, grayBit, componentBits, false, _tuning, destination, ref bitPosition, bitLimit)) return -1;
                        if (!BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], b, grayBit, componentBits, false, _tuning, destination, ref bitPosition, bitLimit)) return -1;
                        if (!BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], c, grayBit, componentBits, false, _tuning, destination, ref bitPosition, bitLimit)) return -1;
                    }
                }

                LastArmatureBits = bitPosition - destinationStart * 8;
                AbsoluteBonesLastFrame = absoluteBones;
                int bodyOffset = (bitPosition + 7) >> 3;
                int requiredEnd = bodyOffset + _positionBytes + _tailBytes;
                if (requiredEnd > destinationStart + MaxBodySize || requiredEnd > destination.Length) return -1;

                Buffer.BlockCopy(payload, 0, destination, bodyOffset, _positionBytes);
                bodyOffset += _positionBytes;
                Buffer.BlockCopy(payload, _tailOffset, destination, bodyOffset, _tailBytes);
                bodyOffset += _tailBytes;

                BasisNumerel.TxState[] old = _states;
                _states = _scratchStates;
                _scratchStates = old;
                return bodyOffset - destinationStart;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly BasisNumerel.Tuning _tuning;
            private readonly Options _options;
            private readonly byte[] _bpc;
            private BasisNumerel.RxState[] _states;
            private BasisNumerel.RxState[] _scratchStates;
            private byte[] _payload;
            private byte[] _payloadScratch;
            private bool[] _boneValid;
            private bool[] _scratchBoneValid;
            private byte[] _bonePredictionAge;
            private byte[] _scratchBonePredictionAge;
            private readonly int _positionBytes;
            private readonly int _rotationBytes;
            private readonly int _tailOffset;
            private readonly int _tailBytes;
            private readonly int _payloadBytes;
            private bool _hasSequence;
            private byte _lastSequence;

            public Decoder(BasisAvatarBitPacking.BitQuality quality, BasisNumerel.Tuning tuning)
                : this(quality, Options.NumerelOnly(tuning))
            {
            }

            public Decoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _tuning = options.Numerel;
                _bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                _positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                _rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                _tailOffset = _positionBytes + _rotationBytes;
                _payloadBytes = BasisBoneRotationCompression.ConvertToSize(quality);
                _tailBytes = _payloadBytes - _tailOffset;
                _states = new BasisNumerel.RxState[StateCount];
                _scratchStates = new BasisNumerel.RxState[StateCount];
                _payload = new byte[_payloadBytes];
                _payloadScratch = new byte[_payloadBytes];
                _boneValid = new bool[BasisBoneRotationCompression.SyncBoneCount];
                _scratchBoneValid = new bool[BasisBoneRotationCompression.SyncBoneCount];
                _bonePredictionAge = new byte[BasisBoneRotationCompression.SyncBoneCount];
                _scratchBonePredictionAge = new byte[BasisBoneRotationCompression.SyncBoneCount];
                Reset();
            }

            public BasisAvatarBitPacking.BitQuality Quality => _quality;
            public int PayloadSize => _payloadBytes;
            public bool HasSequence => _hasSequence;
            public byte LastSequence => _lastSequence;
            public int LastArmatureBits { get; private set; }

            public void Reset()
            {
                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    uint midpoint = 1u << (_bpc[bone] - 1);
                    for (int c = 0; c < ComponentsPerBone; c++)
                    {
                        _states[state].Reset(midpoint);
                        _scratchStates[state].Reset(midpoint);
                        state++;
                    }
                }
                Array.Clear(_payload, 0, _payload.Length);
                Array.Clear(_payloadScratch, 0, _payloadScratch.Length);
                InitializeNeutralArmature(_payload, _positionBytes, _bpc);
                InitializeNeutralArmature(_payloadScratch, _positionBytes, _bpc);
                bool startsTrusted = !UsesPerBoneMode(_options) && _options.ExactDeltaMaxBits == 0;
                for (int bone = 0; bone < _boneValid.Length; bone++)
                {
                    _boneValid[bone] = startsTrusted;
                    _scratchBoneValid[bone] = startsTrusted;
                    _bonePredictionAge[bone] = 0;
                    _scratchBonePredictionAge[bone] = 0;
                }
                _hasSequence = false;
                _lastSequence = 0;
                LastArmatureBits = 0;
            }

            /// <summary>
            /// Decodes one body. Duplicate and stale/reordered packets are rejected before
            /// touching codec state; sequence gaps are accepted and repaired by later Gray bits.
            /// <paramref name="availableBytes"/> may include trailing additional-avatar data.
            /// </summary>
            public bool TryDecode(
                byte[] source,
                int sourceStart,
                int availableBytes,
                byte sequence,
                byte[] outputPayload,
                out int consumedBytes)
            {
                consumedBytes = 0;
                if (source == null || outputPayload == null || outputPayload.Length < _payloadBytes) return false;
                if (sourceStart < 0 || availableBytes < 0 || sourceStart + availableBytes > source.Length) return false;

                int forward = 1;
                if (_hasSequence)
                {
                    forward = (byte)(sequence - _lastSequence);
                    if (forward == 0 || forward >= 128) return false;
                }

                // Every mutable decoder surface is double-buffered. A malformed or truncated
                // packet therefore cannot partially alter prediction state, held pose, or bone
                // validity even when it contains valid absolute records before the failure.
                Array.Copy(_states, _scratchStates, StateCount);
                Buffer.BlockCopy(_payload, 0, _payloadScratch, 0, _payloadBytes);
                Array.Copy(_boneValid, _scratchBoneValid, _boneValid.Length);
                Array.Copy(_bonePredictionAge, _scratchBonePredictionAge, _bonePredictionAge.Length);

                if (forward > 1)
                {
                    // Match upstream NumerelApplyDelta once for every missing sample. V2 exact
                    // low-BPC deltas cannot be advanced safely without the lost packet, so only
                    // Numerel bones extrapolate their hidden predictor across the gap.
                    for (int missing = 1; missing < forward; missing++)
                    {
                        int missingState = 0;
                        for (int bone = 0; bone < _bpc.Length; bone++)
                        {
                            int componentBits = _bpc[bone];
                            if (UsesExactDeltaPath(componentBits, _options))
                            {
                                // The exact-delta base is now stale, but keeping the offset
                                // prediction visible is substantially less disruptive than
                                // freezing every finger/toe until its rotating absolute refresh.
                                missingState += ComponentsPerBone;
                                continue;
                            }
                            for (int component = 0; component < ComponentsPerBone; component++)
                                BasisNumerel.ApplyLastDelta(ref _scratchStates[missingState++], componentBits, false);
                        }
                    }

                    if (_options.MaxPredictionAge > 0)
                    {
                        int missed = forward - 1;
                        for (int bone = 0; bone < _scratchBoneValid.Length; bone++)
                        {
                            int age = _scratchBonePredictionAge[bone] + missed;
                            _scratchBonePredictionAge[bone] = (byte)Math.Min(byte.MaxValue, age);
                            if (_scratchBonePredictionAge[bone] > _options.MaxPredictionAge)
                                _scratchBoneValid[bone] = false;
                        }
                    }
                    else if (UsesPerBoneMode(_options))
                    {
                        // Preserve the first-generation POC behavior for benchmark comparison.
                        for (int bone = 0; bone < _scratchBoneValid.Length; bone++)
                            _scratchBoneValid[bone] = false;
                    }
                }

                int bitPosition = sourceStart * 8;
                int bitLimit = (sourceStart + availableBytes) * 8;
                int destinationBit = _positionBytes * 8;
                int stateIndex = 0;

                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int componentBits = _bpc[bone];
                    bool exactDelta = UsesExactDeltaPath(componentBits, _options);
                    bool absolute = false;
                    if (UsesPerBoneMode(_options))
                    {
                        if (!TryReadRawBits(source, ref bitPosition, bitLimit, 1, out uint mode)) return false;
                        absolute = mode != 0;
                    }

                    if (!TryReadRawBits(source, ref bitPosition, bitLimit, 2, out uint largest)) return false;
                    int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                    uint a, b, c;
                    if (absolute)
                    {
                        if (!TryReadRawBits(source, ref bitPosition, bitLimit, componentBits, out a)
                            || !TryReadRawBits(source, ref bitPosition, bitLimit, componentBits, out b)
                            || !TryReadRawBits(source, ref bitPosition, bitLimit, componentBits, out c)) return false;
                        _scratchStates[stateIndex++].Reset(a);
                        _scratchStates[stateIndex++].Reset(b);
                        _scratchStates[stateIndex++].Reset(c);
                        _scratchBoneValid[bone] = true;
                        _scratchBonePredictionAge[bone] = 0;
                    }
                    else if (exactDelta)
                    {
                        bool staleBase = _scratchBonePredictionAge[bone] > 0 || !_scratchBoneValid[bone];
                        if (!TryReadExactDelta(ref _scratchStates[stateIndex++], componentBits, staleBase, source, ref bitPosition, bitLimit, out a)
                            || !TryReadExactDelta(ref _scratchStates[stateIndex++], componentBits, staleBase, source, ref bitPosition, bitLimit, out b)
                            || !TryReadExactDelta(ref _scratchStates[stateIndex++], componentBits, staleBase, source, ref bitPosition, bitLimit, out c)) return false;
                    }
                    else
                    {
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _tuning, source, ref bitPosition, bitLimit, out a)) return false;
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _tuning, source, ref bitPosition, bitLimit, out b)) return false;
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _tuning, source, ref bitPosition, bitLimit, out c)) return false;

                        if (_options.MaxPredictionAge > 0 && _scratchBonePredictionAge[bone] > 0)
                        {
                            _scratchBonePredictionAge[bone]--;
                            if (!_scratchBoneValid[bone] && _scratchBonePredictionAge[bone] == 0)
                                _scratchBoneValid[bone] = true;
                        }
                    }

                    ulong packed = largest
                        | ((ulong)a << 2)
                        | ((ulong)b << (2 + componentBits))
                        | ((ulong)c << (2 + componentBits * 2));
                    if (_scratchBoneValid[bone])
                        WriteBitsOverwrite(_payloadScratch, destinationBit, packed, 2 + componentBits * 3);
                    destinationBit += 2 + componentBits * 3;
                }

                int armatureBits = bitPosition - sourceStart * 8;
                int bodyOffset = (bitPosition + 7) >> 3;
                int bodyEnd = bodyOffset + _positionBytes + _tailBytes;
                if (bodyEnd > sourceStart + availableBytes || bodyEnd > source.Length) return false;

                Buffer.BlockCopy(source, bodyOffset, _payloadScratch, 0, _positionBytes);
                bodyOffset += _positionBytes;
                Buffer.BlockCopy(source, bodyOffset, _payloadScratch, _tailOffset, _tailBytes);
                bodyOffset += _tailBytes;

                BasisNumerel.RxState[] oldStates = _states;
                _states = _scratchStates;
                _scratchStates = oldStates;

                byte[] oldPayload = _payload;
                _payload = _payloadScratch;
                _payloadScratch = oldPayload;

                bool[] oldBoneValid = _boneValid;
                _boneValid = _scratchBoneValid;
                _scratchBoneValid = oldBoneValid;

                byte[] oldPredictionAge = _bonePredictionAge;
                _bonePredictionAge = _scratchBonePredictionAge;
                _scratchBonePredictionAge = oldPredictionAge;

                _hasSequence = true;
                _lastSequence = sequence;
                LastArmatureBits = armatureBits;
                Buffer.BlockCopy(_payload, 0, outputPayload, 0, _payloadBytes);
                consumedBytes = bodyOffset - sourceStart;
                return true;
            }
        }

        public static int GetMaxBodySize(BasisAvatarBitPacking.BitQuality quality, BasisNumerel.Tuning tuning)
            => GetMaxBodySize(quality, Options.NumerelOnly(tuning));

        public static int GetMaxBodySize(BasisAvatarBitPacking.BitQuality quality, Options options)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            int armatureBits = 0;
            for (int bone = 0; bone < bpc.Length; bone++)
            {
                if (UsesExactDeltaPath(bpc[bone], options))
                {
                    armatureBits += 1 + 2 + ComponentsPerBone * bpc[bone];
                    continue;
                }

                int numerelBits = ComponentsPerBone * BasisNumerel.MaxEncodedBits(bpc[bone], options.Numerel);
                if (UsesPerBoneMode(options))
                    armatureBits += 1 + 2 + Math.Max(numerelBits, ComponentsPerBone * bpc[bone]);
                else
                    armatureBits += 2 + numerelBits;
            }

            int absoluteBytes = BasisAvatarBitPacking.PositionBytes(quality)
                + (BasisBoneRotationCompression.ConvertToSize(quality)
                    - BasisAvatarBitPacking.PositionBytes(quality)
                    - BasisBoneRotationCompression.RotationBytes(quality));
            return ((armatureBits + 7) >> 3) + absoluteBytes;
        }

        public static int GetAbsoluteBytes(BasisAvatarBitPacking.BitQuality quality)
        {
            int position = BasisAvatarBitPacking.PositionBytes(quality);
            return position + BasisBoneRotationCompression.ConvertToSize(quality)
                - position - BasisBoneRotationCompression.RotationBytes(quality);
        }

        private static void InitializeNeutralArmature(byte[] payload, int positionBytes, byte[] bpc)
        {
            int bit = positionBytes * 8;
            for (int bone = 0; bone < bpc.Length; bone++)
            {
                int componentBits = bpc[bone];
                ulong midpoint = 1UL << (componentBits - 1);
                ulong identity = 3UL
                    | (midpoint << 2)
                    | (midpoint << (2 + componentBits))
                    | (midpoint << (2 + componentBits * 2));
                WriteBitsOverwrite(payload, bit, identity, 2 + componentBits * 3);
                bit += 2 + componentBits * 3;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool UsesExactDeltaPath(int componentBits, Options options)
            => options.ExactDeltaMaxBits > 0 && componentBits <= options.ExactDeltaMaxBits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool UsesPerBoneMode(Options options)
            => options.BoneAbsoluteEscape || options.RefreshBonesPerFrame > 0;

        private static bool IsScheduledRefreshBone(int bone, byte sequence, byte[] bpc, Options options)
        {
            int refreshBonesPerFrame = options.RefreshBonesPerFrame;
            if (refreshBonesPerFrame <= 0) return false;
            if (!options.RefreshNumerelBonesOnly)
            {
                int first = sequence * refreshBonesPerFrame % BasisBoneRotationCompression.SyncBoneCount;
                for (int i = 0; i < refreshBonesPerFrame; i++)
                    if ((first + i) % BasisBoneRotationCompression.SyncBoneCount == bone) return true;
                return false;
            }

            if (UsesExactDeltaPath(bpc[bone], options)) return false;
            int eligible = 0;
            int ordinal = -1;
            for (int i = 0; i < bpc.Length; i++)
            {
                if (UsesExactDeltaPath(bpc[i], options)) continue;
                if (i == bone) ordinal = eligible;
                eligible++;
            }
            if (eligible == 0 || ordinal < 0) return false;

            int firstEligible = sequence * refreshBonesPerFrame % eligible;
            int count = Math.Min(refreshBonesPerFrame, eligible);
            for (int i = 0; i < count; i++)
                if ((firstEligible + i) % eligible == ordinal) return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ExactDeltaBitCount(uint estimate, uint value, int componentBits)
            => estimate == value ? 1 : componentBits + 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryWriteExactDelta(
            ref BasisNumerel.TxState state,
            uint value,
            int componentBits,
            byte[] destination,
            ref int bitPosition,
            int bitLimit)
        {
            int delta = (int)value - (int)state.RemoteEstimate;
            if (delta == 0)
                return TryWriteRawBits(destination, ref bitPosition, bitLimit, 0, 1);

            uint magnitude = (uint)Math.Abs(delta);
            if (!TryWriteRawBits(destination, ref bitPosition, bitLimit, 1, 1)
                || !TryWriteRawBits(destination, ref bitPosition, bitLimit, delta < 0 ? 1u : 0u, 1)
                || !TryWriteRawBits(destination, ref bitPosition, bitLimit, magnitude, componentBits))
                return false;
            state.Reset(value);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadExactDelta(
            ref BasisNumerel.RxState state,
            int componentBits,
            bool staleBase,
            byte[] source,
            ref int bitPosition,
            int bitLimit,
            out uint value)
        {
            value = state.RawEstimate;
            if (!TryReadRawBits(source, ref bitPosition, bitLimit, 1, out uint changed)) return false;
            if (changed == 0) return true;
            if (!TryReadRawBits(source, ref bitPosition, bitLimit, 1, out uint negative)
                || !TryReadRawBits(source, ref bitPosition, bitLimit, componentBits, out uint magnitude))
                return false;

            int next = (int)state.RawEstimate + (negative != 0 ? -(int)magnitude : (int)magnitude);
            int max = (1 << componentBits) - 1;
            if (next < 0 || next > max)
            {
                if (!staleBase) return false;
                next = next < 0 ? 0 : max;
            }
            value = (uint)next;
            state.Reset(value);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryWriteRawBits(byte[] destination, ref int bitPosition, int bitLimit, uint value, int count)
        {
            if (bitPosition < 0 || count < 0 || bitPosition + count > bitLimit || bitLimit > destination.Length * 8) return false;
            for (int i = count - 1; i >= 0; i--)
            {
                int byteIndex = bitPosition >> 3;
                int bitInByte = 7 - (bitPosition & 7);
                if (((value >> i) & 1u) != 0) destination[byteIndex] |= (byte)(1 << bitInByte);
                bitPosition++;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadRawBits(byte[] source, ref int bitPosition, int bitLimit, int count, out uint value)
        {
            value = 0;
            if (bitPosition < 0 || count < 0 || bitPosition + count > bitLimit || bitLimit > source.Length * 8) return false;
            for (int i = 0; i < count; i++)
            {
                int byteIndex = bitPosition >> 3;
                int bitInByte = 7 - (bitPosition & 7);
                value = (value << 1) | (uint)((source[byteIndex] >> bitInByte) & 1);
                bitPosition++;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBitsOverwrite(byte[] destination, int bitPosition, ulong value, int count)
        {
            int bytePosition = bitPosition >> 3;
            int bitInByte = bitPosition & 7;
            int left = count;
            while (left > 0)
            {
                int room = 8 - bitInByte;
                int take = left < room ? left : room;
                int lowMask = (1 << take) - 1;
                int clearMask = lowMask << bitInByte;
                byte chunk = (byte)((int)(value & (ulong)lowMask) << bitInByte);
                destination[bytePosition] = (byte)((destination[bytePosition] & ~clearMask) | chunk);
                value >>= take;
                left -= take;
                bytePosition++;
                bitInByte = 0;
            }
        }
    }
}
