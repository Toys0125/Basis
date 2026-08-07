using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Experimental Numerel armature codec that codes all four quaternion components independently.
    /// The input/output payload remains Basis smallest-three so existing capture and benchmark tooling
    /// can compare this representation directly with the current armature codec.
    /// </summary>
    public static class BasisNumerelQuaternion4ArmatureCodec
    {
        private const int ComponentsPerBone = 4;
        private const int StateCount = BasisBoneRotationCompression.SyncBoneCount * ComponentsPerBone;
        private const int MaxPredictedGapFrames = 32;

        public readonly struct Options
        {
            public readonly BasisNumerel.Tuning Numerel;
            public readonly bool PreserveSignContinuity;
            public readonly sbyte ComponentBitsAdjustment;
            public readonly bool AdaptivePrecision;
            public readonly byte FixedComponentBits;

            public Options(BasisNumerel.Tuning numerel, bool preserveSignContinuity, sbyte componentBitsAdjustment = 0, bool adaptivePrecision = false, byte fixedComponentBits = 0)
            {
                if (componentBitsAdjustment < -3 || componentBitsAdjustment > 4)
                    throw new ArgumentOutOfRangeException(nameof(componentBitsAdjustment));
                if (fixedComponentBits != 0 && (fixedComponentBits < 3 || fixedComponentBits > 30))
                    throw new ArgumentOutOfRangeException(nameof(fixedComponentBits));
                Numerel = numerel;
                PreserveSignContinuity = preserveSignContinuity;
                ComponentBitsAdjustment = componentBitsAdjustment;
                AdaptivePrecision = adaptivePrecision;
                FixedComponentBits = fixedComponentBits;
            }

            public static Options Upstream => new Options(BasisNumerel.Tuning.Reference, false, 0);
            public static Options UpstreamContinuous => new Options(BasisNumerel.Tuning.Reference, true, 0);
            public static Options UpstreamContinuousMinus1 => new Options(BasisNumerel.Tuning.Reference, true, -1);
            public static Options UpstreamContinuousMinus2 => new Options(BasisNumerel.Tuning.Reference, true, -2);
            public static Options UpstreamContinuousPlus1 => new Options(BasisNumerel.Tuning.Reference, true, 1);
            public static Options UpstreamContinuousPlus2 => new Options(BasisNumerel.Tuning.Reference, true, 2);
            public static Options UpstreamContinuousAdaptive => new Options(BasisNumerel.Tuning.Reference, true, 0, true);
            public static Options UpstreamContinuous12Bit => new Options(BasisNumerel.Tuning.Reference, true, fixedComponentBits: 12);
            public static Options SquareRoot04Continuous12Bit => new Options(BasisNumerel.Tuning.SquareRoot04Reference, true, fixedComponentBits: 12);
            public static Options NearestSquareRootContinuous12Bit => new Options(BasisNumerel.Tuning.NearestSquareRootReference, true, fixedComponentBits: 12);
        }

        public sealed class Encoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly byte[] _bpc;
            private readonly int[] _componentBits;
            private readonly int _maxBodySize;
            private BasisNumerel.TxState[] _states;
            private BasisNumerel.TxState[] _scratchStates;
            private float[] _previousQuaternion;
            private float[] _scratchPreviousQuaternion;
            private readonly int _positionBytes;
            private readonly int _rotationBytes;
            private readonly int _tailOffset;
            private readonly int _tailBytes;
            private readonly int _payloadBytes;

            public Encoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                _componentBits = BuildComponentBits(_bpc, options);
                _maxBodySize = GetMaxBodySize(quality, options);
                _positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                _rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                _tailOffset = _positionBytes + _rotationBytes;
                _payloadBytes = BasisBoneRotationCompression.ConvertToSize(quality);
                _tailBytes = _payloadBytes - _tailOffset;
                _states = new BasisNumerel.TxState[StateCount];
                _scratchStates = new BasisNumerel.TxState[StateCount];
                _previousQuaternion = new float[StateCount];
                _scratchPreviousQuaternion = new float[StateCount];
                Reset();
            }

            public int PayloadSize => _payloadBytes;
            public int MaxBodySize => _maxBodySize;
            public int LastArmatureBits { get; private set; }

            public void Reset()
            {
                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int bits = _componentBits[bone];
                    uint midpoint = QuantizeComponent(0f, bits);
                    uint one = QuantizeComponent(1f, bits);
                    _states[state].Reset(midpoint);
                    _scratchStates[state].Reset(midpoint);
                    _previousQuaternion[state] = 0f;
                    _scratchPreviousQuaternion[state++] = 0f;
                    _states[state].Reset(midpoint);
                    _scratchStates[state].Reset(midpoint);
                    _previousQuaternion[state] = 0f;
                    _scratchPreviousQuaternion[state++] = 0f;
                    _states[state].Reset(midpoint);
                    _scratchStates[state].Reset(midpoint);
                    _previousQuaternion[state] = 0f;
                    _scratchPreviousQuaternion[state++] = 0f;
                    _states[state].Reset(one);
                    _scratchStates[state].Reset(one);
                    _previousQuaternion[state] = 1f;
                    _scratchPreviousQuaternion[state++] = 1f;
                }
                LastArmatureBits = 0;
            }

            public int Encode(byte[] payload, byte sequence, byte[] destination, int destinationStart)
            {
                if (payload == null || payload.Length < _payloadBytes || destination == null || destinationStart < 0)
                    return -1;
                if (destination.Length - destinationStart < MaxBodySize) return -1;

                Array.Copy(_states, _scratchStates, StateCount);
                Array.Copy(_previousQuaternion, _scratchPreviousQuaternion, StateCount);
                Array.Clear(destination, destinationStart, MaxBodySize);

                int bitPosition = destinationStart * 8;
                int bitLimit = (destinationStart + MaxBodySize) * 8;
                int sourceBit = _positionBytes * 8;
                int stateIndex = 0;

                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int sourceBits = _bpc[bone];
                    int width = 2 + sourceBits * 3;
                    ulong packed = BasisBoneRotationCompression.ReadBits(payload, ref sourceBit, width);
                    BasisBoneRotationCompression.DecodeSmallestThree(
                        packed, sourceBits,
                        out float qx, out float qy, out float qz, out float qw,
                        BasisBoneRotationCompression.MAX_COMPONENT[bone]);

                    if (_options.PreserveSignContinuity)
                    {
                        float dot = qx * _scratchPreviousQuaternion[stateIndex]
                            + qy * _scratchPreviousQuaternion[stateIndex + 1]
                            + qz * _scratchPreviousQuaternion[stateIndex + 2]
                            + qw * _scratchPreviousQuaternion[stateIndex + 3];
                        if (dot < 0f)
                        {
                            qx = -qx;
                            qy = -qy;
                            qz = -qz;
                            qw = -qw;
                        }
                    }

                    _scratchPreviousQuaternion[stateIndex] = qx;
                    _scratchPreviousQuaternion[stateIndex + 1] = qy;
                    _scratchPreviousQuaternion[stateIndex + 2] = qz;
                    _scratchPreviousQuaternion[stateIndex + 3] = qw;

                    int componentBits = _componentBits[bone];
                    int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                    uint x = QuantizeComponent(qx, componentBits);
                    uint y = QuantizeComponent(qy, componentBits);
                    uint z = QuantizeComponent(qz, componentBits);
                    uint w = QuantizeComponent(qw, componentBits);

                    if (!BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], x, grayBit, componentBits, false, _options.Numerel, destination, ref bitPosition, bitLimit)
                        || !BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], y, grayBit, componentBits, false, _options.Numerel, destination, ref bitPosition, bitLimit)
                        || !BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], z, grayBit, componentBits, false, _options.Numerel, destination, ref bitPosition, bitLimit)
                        || !BasisNumerel.TryEncode(ref _scratchStates[stateIndex++], w, grayBit, componentBits, false, _options.Numerel, destination, ref bitPosition, bitLimit))
                        return -1;
                }

                int armatureBits = bitPosition - destinationStart * 8;
                int bodyOffset = (bitPosition + 7) >> 3;
                int requiredEnd = bodyOffset + _positionBytes + _tailBytes;
                if (requiredEnd > destinationStart + MaxBodySize || requiredEnd > destination.Length) return -1;

                Buffer.BlockCopy(payload, 0, destination, bodyOffset, _positionBytes);
                bodyOffset += _positionBytes;
                Buffer.BlockCopy(payload, _tailOffset, destination, bodyOffset, _tailBytes);
                bodyOffset += _tailBytes;

                BasisNumerel.TxState[] oldStates = _states;
                _states = _scratchStates;
                _scratchStates = oldStates;
                float[] oldPrevious = _previousQuaternion;
                _previousQuaternion = _scratchPreviousQuaternion;
                _scratchPreviousQuaternion = oldPrevious;
                LastArmatureBits = armatureBits;
                return bodyOffset - destinationStart;
            }
        }

        public sealed class Decoder
        {
            private readonly BasisAvatarBitPacking.BitQuality _quality;
            private readonly Options _options;
            private readonly byte[] _bpc;
            private readonly int[] _componentBits;
            private BasisNumerel.RxState[] _states;
            private BasisNumerel.RxState[] _scratchStates;
            private byte[] _payload;
            private byte[] _payloadScratch;
            private readonly int _positionBytes;
            private readonly int _rotationBytes;
            private readonly int _tailOffset;
            private readonly int _tailBytes;
            private readonly int _payloadBytes;
            private bool _hasSequence;
            private byte _lastSequence;

            public Decoder(BasisAvatarBitPacking.BitQuality quality, Options options)
            {
                _quality = quality;
                _options = options;
                _bpc = BasisBoneRotationCompression.GetBpcTable(quality);
                _componentBits = BuildComponentBits(_bpc, options);
                _positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
                _rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
                _tailOffset = _positionBytes + _rotationBytes;
                _payloadBytes = BasisBoneRotationCompression.ConvertToSize(quality);
                _tailBytes = _payloadBytes - _tailOffset;
                _states = new BasisNumerel.RxState[StateCount];
                _scratchStates = new BasisNumerel.RxState[StateCount];
                _payload = new byte[_payloadBytes];
                _payloadScratch = new byte[_payloadBytes];
                Reset();
            }

            public int PayloadSize => _payloadBytes;
            public bool HasSequence => _hasSequence;
            public byte LastSequence => _lastSequence;
            public int LastArmatureBits { get; private set; }

            public void CopyDisplayedPose(byte[] outputPayload)
            {
                if (outputPayload == null || outputPayload.Length < _payloadBytes)
                    throw new ArgumentException("Output payload is too small.", nameof(outputPayload));
                Buffer.BlockCopy(_payload, 0, outputPayload, 0, _payloadBytes);
            }

            public void Reset()
            {
                int state = 0;
                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int bits = _componentBits[bone];
                    uint midpoint = QuantizeComponent(0f, bits);
                    uint one = QuantizeComponent(1f, bits);
                    _states[state].Reset(midpoint);
                    _scratchStates[state++].Reset(midpoint);
                    _states[state].Reset(midpoint);
                    _scratchStates[state++].Reset(midpoint);
                    _states[state].Reset(midpoint);
                    _scratchStates[state++].Reset(midpoint);
                    _states[state].Reset(one);
                    _scratchStates[state++].Reset(one);
                }
                Array.Clear(_payload, 0, _payload.Length);
                Array.Clear(_payloadScratch, 0, _payloadScratch.Length);
                InitializeNeutralArmature(_payload, _positionBytes, _bpc);
                InitializeNeutralArmature(_payloadScratch, _positionBytes, _bpc);
                _hasSequence = false;
                _lastSequence = 0;
                LastArmatureBits = 0;
            }

            public bool TryDecode(byte[] source, int sourceStart, int availableBytes, byte sequence, byte[] outputPayload, out int consumedBytes)
            {
                consumedBytes = 0;
                if (source == null || outputPayload == null || outputPayload.Length < _payloadBytes) return false;
                if (sourceStart < 0 || availableBytes < 0 || sourceStart + availableBytes > source.Length) return false;

                int forward = 1;
                if (_hasSequence)
                {
                    forward = (byte)(sequence - _lastSequence);
                    if (forward == 0 || forward >= 128) return false;
                    if (forward - 1 > MaxPredictedGapFrames) return false;
                }

                Array.Copy(_states, _scratchStates, StateCount);
                Buffer.BlockCopy(_payload, 0, _payloadScratch, 0, _payloadBytes);

                if (forward > 1)
                {
                    for (int missing = 1; missing < forward; missing++)
                    {
                        int state = 0;
                        for (int bone = 0; bone < _bpc.Length; bone++)
                        {
                            int componentBits = _componentBits[bone];
                            for (int component = 0; component < ComponentsPerBone; component++)
                                BasisNumerel.ApplyLastDelta(ref _scratchStates[state++], componentBits, false);
                        }
                    }
                }

                int bitPosition = sourceStart * 8;
                int bitLimit = (sourceStart + availableBytes) * 8;
                int destinationBit = _positionBytes * 8;
                int stateIndex = 0;

                for (int bone = 0; bone < _bpc.Length; bone++)
                {
                    int componentBits = _componentBits[bone];
                    int grayBit = BasisNumerel.GrayScramble(sequence % componentBits, componentBits);
                    if (!BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out uint x)
                        || !BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out uint y)
                        || !BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out uint z)
                        || !BasisNumerel.TryDecode(ref _scratchStates[stateIndex++], grayBit, componentBits, false, _options.Numerel, source, ref bitPosition, bitLimit, out uint w))
                        return false;

                    float qx = DequantizeComponent(x, componentBits);
                    float qy = DequantizeComponent(y, componentBits);
                    float qz = DequantizeComponent(z, componentBits);
                    float qw = DequantizeComponent(w, componentBits);
                    Normalize(ref qx, ref qy, ref qz, ref qw);

                    int outputBits = _bpc[bone];
                    ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
                        qx, qy, qz, qw, outputBits, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                    WriteBitsOverwrite(_payloadScratch, destinationBit, packed, 2 + outputBits * 3);
                    destinationBit += 2 + outputBits * 3;
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

                _hasSequence = true;
                _lastSequence = sequence;
                LastArmatureBits = armatureBits;
                Buffer.BlockCopy(_payload, 0, outputPayload, 0, _payloadBytes);
                consumedBytes = bodyOffset - sourceStart;
                return true;
            }
        }

        public static int GetMaxBodySize(BasisAvatarBitPacking.BitQuality quality, Options options)
        {
            byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
            int armatureBits = 0;
            for (int bone = 0; bone < bpc.Length; bone++)
            {
                int componentBits = ComponentBits(bpc[bone], options);
                // Upstream non-looping transmitter estimates may temporarily overshoot the nominal
                // scalar range. A 32-bit budget per scalar safely covers the full int-domain cube
                // root code plus up to eight Gray bits without affecting actual encoded size.
                armatureBits += ComponentsPerBone * Math.Max(32, BasisNumerel.MaxEncodedBits(componentBits, options.Numerel));
            }
            int position = BasisAvatarBitPacking.PositionBytes(quality);
            int absoluteBytes = position + BasisBoneRotationCompression.ConvertToSize(quality)
                - position - BasisBoneRotationCompression.RotationBytes(quality);
            return ((armatureBits + 7) >> 3) + absoluteBytes;
        }

        private static int[] BuildComponentBits(byte[] sourceBits, Options options)
        {
            var result = new int[sourceBits.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = ComponentBits(sourceBits[i], options);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComponentBits(int sourceBits, Options options)
        {
            if (options.FixedComponentBits != 0) return options.FixedComponentBits;
            int adjustment = options.AdaptivePrecision
                ? (sourceBits <= 6 ? 2 : 1)
                : options.ComponentBitsAdjustment;
            return Math.Max(3, Math.Min(30, sourceBits + adjustment));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint QuantizeComponent(float value, int bits)
        {
            float clamped = value < -1f ? -1f : value > 1f ? 1f : value;
            uint max = (1u << bits) - 1u;
            double scaled = (clamped * 0.5 + 0.5) * max;
            long quantized = (long)Math.Floor(scaled + 0.5);
            if (quantized < 0) return 0;
            if ((ulong)quantized > max) return max;
            return (uint)quantized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float DequantizeComponent(uint value, int bits)
        {
            uint max = (1u << bits) - 1u;
            return (float)(value / (double)max * 2.0 - 1.0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Normalize(ref float x, ref float y, ref float z, ref float w)
        {
            double lengthSquared = x * x + y * y + z * z + w * w;
            if (lengthSquared <= 1e-16)
            {
                x = 0f; y = 0f; z = 0f; w = 1f;
                return;
            }
            float inverse = (float)(1.0 / Math.Sqrt(lengthSquared));
            x *= inverse; y *= inverse; z *= inverse; w *= inverse;
        }

        private static void InitializeNeutralArmature(byte[] payload, int positionBytes, byte[] bpc)
        {
            int bit = positionBytes * 8;
            for (int bone = 0; bone < bpc.Length; bone++)
            {
                ulong identity = BasisBoneRotationCompression.EncodeSmallestThree(
                    0f, 0f, 0f, 1f, bpc[bone], BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                WriteBitsOverwrite(payload, bit, identity, 2 + bpc[bone] * 3);
                bit += 2 + bpc[bone] * 3;
            }
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
