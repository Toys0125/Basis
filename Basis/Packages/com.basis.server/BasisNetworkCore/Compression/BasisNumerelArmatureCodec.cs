using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Experimental keyframe-free armature stream built on <see cref="BasisNumerel"/>.
    ///
    /// Each of the 51 existing smallest-three bone values is split into:
    ///   - 2 exact bits for the omitted quaternion component index;
    ///   - three Numerel-coded quantized components at the quality's existing BPC.
    ///
    /// Every bone is present in every frame. A zero temporal delta costs only the Numerel
    /// zero code plus Gray correction bits, which also lets a late receiver converge from
    /// a neutral midpoint without ever receiving a full armature keyframe. Position, scale,
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

            public Options(BasisNumerel.Tuning numerel, bool boneAbsoluteEscape, ushort residualDivisor = 512, byte refreshBonesPerFrame = 0)
            {
                if (boneAbsoluteEscape && residualDivisor < 2) throw new ArgumentOutOfRangeException(nameof(residualDivisor));
                if (refreshBonesPerFrame > BasisBoneRotationCompression.SyncBoneCount) throw new ArgumentOutOfRangeException(nameof(refreshBonesPerFrame));
                Numerel = numerel;
                BoneAbsoluteEscape = boneAbsoluteEscape;
                ResidualDivisor = residualDivisor;
                RefreshBonesPerFrame = refreshBonesPerFrame;
            }

            public static Options NumerelOnly(BasisNumerel.Tuning tuning) => new Options(tuning, false, 2, 0);
            public static Options HybridPoc => new Options(new BasisNumerel.Tuning(1, -1, true, false), true, 512, 4);
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
                    bool absolute = IsScheduledRefreshBone(bone, sequence, _options.RefreshBonesPerFrame);
                    if (_options.BoneAbsoluteEscape)
                    {
                        // Zero is intentional for coarse BPC bones: one quantized step can already
                        // be several degrees, so those bones use the local absolute escape whenever
                        // the Numerel prediction would miss by even one code point.
                        int threshold = (int)((mask + 1u) / _options.ResidualDivisor);
                        uint predictedA = BasisNumerel.PredictRemoteEstimate(_scratchStates[stateIndex].RemoteEstimate, a, grayBit, componentBits, false, _tuning);
                        uint predictedB = BasisNumerel.PredictRemoteEstimate(_scratchStates[stateIndex + 1].RemoteEstimate, b, grayBit, componentBits, false, _tuning);
                        uint predictedC = BasisNumerel.PredictRemoteEstimate(_scratchStates[stateIndex + 2].RemoteEstimate, c, grayBit, componentBits, false, _tuning);
                        absolute |= Math.Abs((int)a - (int)predictedA) > threshold
                            || Math.Abs((int)b - (int)predictedB) > threshold
                            || Math.Abs((int)c - (int)predictedC) > threshold;
                    }
                    if (_options.BoneAbsoluteEscape || _options.RefreshBonesPerFrame > 0)
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
                bool startsTrusted = !_options.BoneAbsoluteEscape && _options.RefreshBonesPerFrame == 0;
                for (int bone = 0; bone < _boneValid.Length; bone++)
                {
                    _boneValid[bone] = startsTrusted;
                    _scratchBoneValid[bone] = startsTrusted;
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

                if (forward > 1)
                {
                    // Match upstream NumerelApplyDelta once for every missing sample. Each bone
                    // component has its own bit width and last delta, so prediction must advance
                    // per scalar rather than once for the whole armature.
                    for (int missing = 1; missing < forward; missing++)
                    {
                        int missingState = 0;
                        for (int bone = 0; bone < _bpc.Length; bone++)
                        {
                            int componentBits = _bpc[bone];
                            for (int component = 0; component < ComponentsPerBone; component++)
                                BasisNumerel.ApplyLastDelta(ref _scratchStates[missingState++], componentBits, false);
                        }
                    }

                    if (_options.BoneAbsoluteEscape || _options.RefreshBonesPerFrame > 0)
                    {
                        // Hybrid mode additionally conceals uncertain predictions. The hidden
                        // Numerel states continue advancing, but the last valid displayed bone is
                        // held until a local absolute escape or rotating refresh re-establishes it.
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
                    bool absolute = false;
                    if (_options.BoneAbsoluteEscape || _options.RefreshBonesPerFrame > 0)
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
                    }
                    else
                    {
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _tuning, source, ref bitPosition, bitLimit, out a)) return false;
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _tuning, source, ref bitPosition, bitLimit, out b)) return false;
                        if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _tuning, source, ref bitPosition, bitLimit, out c)) return false;
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
                int numerelBits = ComponentsPerBone * BasisNumerel.MaxEncodedBits(bpc[bone], options.Numerel);
                if (options.BoneAbsoluteEscape || options.RefreshBonesPerFrame > 0)
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
        private static bool IsScheduledRefreshBone(int bone, byte sequence, int refreshBonesPerFrame)
        {
            if (refreshBonesPerFrame <= 0) return false;
            int first = sequence * refreshBonesPerFrame % BasisBoneRotationCompression.SyncBoneCount;
            for (int i = 0; i < refreshBonesPerFrame; i++)
                if ((first + i) % BasisBoneRotationCompression.SyncBoneCount == bone) return true;
            return false;
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
