using System;
using System.Runtime.CompilerServices;

namespace Basis.Network.Core.Compression
{
    /// <summary>
    /// Safe C# port of cnlohr/numerel at upstream revision
    /// 8676848ae268f3a8eee672413f272ee422521d09 (2026-08-07).
    ///
    /// Upstream source: https://codeberg.org/cnlohr/numerel
    /// Upstream license: MIT, Copyright (c) 2026 cnlohr.
    ///
    /// Numerel encodes a lossy cube-root delta with an exponential-Golomb-like code and
    /// appends one Gray-code estimate bit. The receiver can reapply its last decoded delta
    /// for a missing sample, then later self-heal its estimate as different Gray bits arrive.
    ///
    /// <see cref="Tuning.Reference"/> preserves the upstream algorithm, including its
    /// literal ((int)(pow(v, 0.3333333)+0.4)) cube-root approximation. Other tuning modes are Basis
    /// experiments and are not wire-compatible with the upstream reference mode.
    /// </summary>
    public static class BasisNumerel
    {
        public const string UpstreamRevision = "8676848ae268f3a8eee672413f272ee422521d09";

        public struct TxState
        {
            public uint RemoteEstimate;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset(uint estimate) => RemoteEstimate = estimate;
        }

        public struct RxState
        {
            public uint RawEstimate;
            public uint OutputValue;
            public int LastDelta;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Reset(uint estimate)
            {
                RawEstimate = estimate;
                OutputValue = estimate;
                LastDelta = 0;
            }
        }

        public enum DifferenceCompressionMode : byte
        {
            /// <summary>Matches upstream NumerelDiffCompress: (int)(pow(abs(v), 0.3333333) + 0.4).</summary>
            UpstreamPow = 0,
            /// <summary>Deterministic mathematical floor cube root; Basis experimental mode.</summary>
            FloorCubeRoot = 1,
            /// <summary>Deterministic nearest integer cube root; Basis experimental mode.</summary>
            NearestCubeRoot = 2,
            /// <summary>Square-root analogue of upstream: (int)(sqrt(abs(v)) + 0.4), reconstructed by signed square.</summary>
            SquareRoot04 = 3,
            /// <summary>Nearest integer square root, reconstructed by signed square.</summary>
            NearestSquareRoot = 4,
        }

        /// <summary>
        /// Codec tuning. Reference Numerel uses one Gray bit, a quarter-step output filter,
        /// the upstream pow-based cube root, and Gray correction sourced from the reconstructed
        /// transmitter estimate. Multiple Gray bits, output snap, integer cube roots, and
        /// source-value correction are Basis-only experiments.
        /// </summary>
        public readonly struct Tuning
        {
            public readonly byte GrayBits;
            public readonly sbyte OutputFilterShift;
            public readonly DifferenceCompressionMode CompressionMode;
            public readonly bool CorrectEstimateFromValueGray;

            public Tuning(
                byte grayBits,
                sbyte outputFilterShift,
                DifferenceCompressionMode compressionMode,
                bool correctEstimateFromValueGray)
            {
                if (grayBits < 1 || grayBits > 8) throw new ArgumentOutOfRangeException(nameof(grayBits));
                if (outputFilterShift < -1 || outputFilterShift > 8) throw new ArgumentOutOfRangeException(nameof(outputFilterShift));
                if (compressionMode < DifferenceCompressionMode.UpstreamPow
                    || compressionMode > DifferenceCompressionMode.NearestSquareRoot)
                    throw new ArgumentOutOfRangeException(nameof(compressionMode));

                GrayBits = grayBits;
                OutputFilterShift = outputFilterShift;
                CompressionMode = compressionMode;
                CorrectEstimateFromValueGray = correctEstimateFromValueGray;
            }

            /// <summary>
            /// Compatibility constructor retained for the existing benchmark:
            /// false selects deterministic floor, true selects deterministic nearest.
            /// </summary>
            public Tuning(byte grayBits, sbyte outputFilterShift, bool nearestCubeRoot, bool correctEstimateFromValueGray)
                : this(
                    grayBits,
                    outputFilterShift,
                    nearestCubeRoot ? DifferenceCompressionMode.NearestCubeRoot : DifferenceCompressionMode.FloorCubeRoot,
                    correctEstimateFromValueGray)
            {
            }

            public static Tuning Reference => new Tuning(
                1,
                2,
                DifferenceCompressionMode.UpstreamPow,
                false);

            public static Tuning ArmaturePoc => new Tuning(
                2,
                -1,
                DifferenceCompressionMode.NearestCubeRoot,
                false);

            public static Tuning SquareRoot04Reference => new Tuning(
                1,
                2,
                DifferenceCompressionMode.SquareRoot04,
                false);

            public static Tuning NearestSquareRootReference => new Tuning(
                1,
                2,
                DifferenceCompressionMode.NearestSquareRoot,
                false);
        }

        /// <summary>
        /// Upstream NumerelGrayScramble. The caller passes frame modulo scalar bit width.
        /// It alternates correction across high and low Gray-code bits instead of scanning
        /// linearly from least significant to most significant.
        /// </summary>
        public static int GrayScramble(int value, int bits)
        {
            if (bits < 1 || bits > 30) throw new ArgumentOutOfRangeException(nameof(bits));
            if (value < 0 || value >= bits) throw new ArgumentOutOfRangeException(nameof(value));
            return (value & 1) != 0 ? value : bits - 2 + (bits & 1) - value;
        }

        /// <summary>
        /// Writes one scalar at <paramref name="bitPosition"/> using an MSB-first bitstream.
        /// The operation is transactional: state and bit position change only after capacity
        /// is validated. Written zero bits are explicitly cleared, so reused buffers are safe.
        /// </summary>
        public static bool TryEncode(
            ref TxState tx,
            uint value,
            int grayBit,
            int numBits,
            bool looping,
            in Tuning tuning,
            byte[] destination,
            ref int bitPosition,
            int bitLimit)
        {
            if (destination == null || numBits < 1 || numBits > 30) return false;
            if (grayBit < 0 || grayBit >= numBits) return false;
            if (bitPosition < 0 || bitLimit < bitPosition || bitLimit > destination.Length * 8) return false;

            uint valueMask = (1u << numBits) - 1u;
            value &= valueMask;
            uint estimate = UsesUnclampedTransmitterEstimate(tuning.CompressionMode)
                ? tx.RemoteEstimate
                : tx.RemoteEstimate & valueMask;

            int difference = (int)value - (int)estimate;
            if (looping)
            {
                difference &= (int)valueMask;
                int scalarLimit = 1 << numBits;
                if (difference > (scalarLimit >> 1)) difference -= scalarLimit;
            }

            int compressed = CompressDifference(difference, tuning.CompressionMode);
            int reconstructedDelta = DecompressDifference(compressed, tuning.CompressionMode);
            estimate = UsesUnclampedTransmitterEstimate(tuning.CompressionMode)
                ? ApplyUpstreamTransmitterDelta(estimate, reconstructedDelta, valueMask, looping)
                : ApplyDeltaToValue(estimate, reconstructedDelta, numBits, looping);

            uint encoded;
            if (compressed == 0)
            {
                encoded = 1;
            }
            else if (compressed < 0)
            {
                encoded = ((uint)(-compressed) << 1) | 1u;
            }
            else
            {
                encoded = (uint)compressed << 1;
            }

            int encodedBits = BitLength(encoded);
            int required = (encodedBits - 1) + encodedBits + tuning.GrayBits;
            if (bitPosition + required > bitLimit) return false;

            int writePosition = bitPosition;

            // Upstream transmits (encodedBits-1) zero prefix bits, the encoded signed value,
            // then one Gray estimate bit. Basis experimental modes may append more Gray bits.
            for (int i = 1; i < encodedBits; i++) WriteBit(destination, ref writePosition, 0);
            for (int i = encodedBits - 1; i >= 0; i--)
                WriteBit(destination, ref writePosition, (int)((encoded >> i) & 1u));

            uint estimateGray = ToGrayCode(estimate);
            uint correctionGray = tuning.CorrectEstimateFromValueGray ? ToGrayCode(value) : estimateGray;
            for (int i = 0; i < tuning.GrayBits; i++)
            {
                int correctionBit = (grayBit + i) % numBits;
                int bit = (int)((correctionGray >> correctionBit) & 1u);
                WriteBit(destination, ref writePosition, bit);
                if (tuning.CorrectEstimateFromValueGray)
                    estimateGray = (estimateGray & ~(1u << correctionBit)) | ((uint)bit << correctionBit);
            }

            if (tuning.CorrectEstimateFromValueGray)
                estimate = FromGrayCode(estimateGray) & valueMask;

            tx.RemoteEstimate = estimate;
            bitPosition = writePosition;
            return true;
        }

        /// <summary>
        /// Reads one scalar from an MSB-first bitstream. State and bit position are committed
        /// only after the complete code, including all Gray correction bits, validates.
        /// </summary>
        public static bool TryDecode(
            ref RxState rx,
            int grayBit,
            int numBits,
            bool looping,
            in Tuning tuning,
            byte[] source,
            ref int bitPosition,
            int bitLimit,
            out uint output)
        {
            output = 0;
            if (source == null || numBits < 1 || numBits > 30) return false;
            if (grayBit < 0 || grayBit >= numBits) return false;
            if (bitPosition < 0 || bitPosition >= bitLimit || bitLimit > source.Length * 8) return false;

            int readPosition = bitPosition;
            RxState next = rx;

            int leadingZeroes = 0;
            while (true)
            {
                if (!TryReadBit(source, ref readPosition, bitLimit, out int bit)) return false;
                if (bit != 0) break;
                leadingZeroes++;
                if (leadingZeroes > 31) return false;
            }

            int encodedBits = leadingZeroes + 1;
            uint encoded = 1;
            for (int i = 1; i < encodedBits; i++)
            {
                if (!TryReadBit(source, ref readPosition, bitLimit, out int bit)) return false;
                encoded = (encoded << 1) | (uint)bit;
            }

            int compressed = (encoded & 1u) != 0 ? -(int)(encoded >> 1) : (int)(encoded >> 1);
            next.LastDelta = DecompressDifference(compressed, tuning.CompressionMode);
            ApplyLastDelta(ref next, numBits, looping);

            uint estimateGray = ToGrayCode(next.RawEstimate);
            for (int i = 0; i < tuning.GrayBits; i++)
            {
                if (!TryReadBit(source, ref readPosition, bitLimit, out int bit)) return false;
                int correctionBit = (grayBit + i) % numBits;
                estimateGray = (estimateGray & ~(1u << correctionBit)) | ((uint)bit << correctionBit);
            }

            uint valueMask = (1u << numBits) - 1u;
            uint estimate = FromGrayCode(estimateGray) & valueMask;

            if (tuning.OutputFilterShift < 0)
            {
                next.OutputValue = estimate;
            }
            else
            {
                int outputDifference = (int)estimate - (int)next.OutputValue;
                if (looping)
                {
                    outputDifference &= (int)valueMask;
                    int scalarLimit = 1 << numBits;
                    if (outputDifference > (scalarLimit >> 1)) outputDifference -= scalarLimit;
                }

                int adjustment = outputDifference >> tuning.OutputFilterShift;
                next.OutputValue = adjustment == 0
                    ? estimate
                    : ApplyDeltaToValue(next.OutputValue, adjustment, numBits, looping);
            }

            next.RawEstimate = estimate;
            rx = next;
            bitPosition = readPosition;
            output = next.OutputValue;
            return true;
        }

        /// <summary>
        /// Upstream NumerelApplyDelta. Call once for each missing sample before decoding the
        /// next received sample. This predicts both the raw estimate and displayed output with
        /// the most recently decoded delta; it does not alter <see cref="RxState.LastDelta"/>.
        /// </summary>
        public static void ApplyLastDelta(ref RxState rx, int numBits, bool looping)
        {
            if (numBits < 1 || numBits > 30) throw new ArgumentOutOfRangeException(nameof(numBits));
            rx.RawEstimate = ApplyDeltaToValue(rx.RawEstimate, rx.LastDelta, numBits, looping);
            rx.OutputValue = ApplyDeltaToValue(rx.OutputValue, rx.LastDelta, numBits, looping);
        }

        /// <summary>
        /// Predicts the transmitter estimate that <see cref="TryEncode"/> will hold after
        /// coding this value, without mutating state or writing bits.
        /// </summary>
        public static uint PredictRemoteEstimate(
            uint remoteEstimate,
            uint value,
            int grayBit,
            int numBits,
            bool looping,
            in Tuning tuning)
        {
            if (numBits < 1 || numBits > 30) throw new ArgumentOutOfRangeException(nameof(numBits));
            if (grayBit < 0 || grayBit >= numBits) throw new ArgumentOutOfRangeException(nameof(grayBit));

            uint valueMask = (1u << numBits) - 1u;
            value &= valueMask;
            uint estimate = UsesUnclampedTransmitterEstimate(tuning.CompressionMode)
                ? remoteEstimate
                : remoteEstimate & valueMask;
            int difference = (int)value - (int)estimate;
            if (looping)
            {
                difference &= (int)valueMask;
                int scalarLimit = 1 << numBits;
                if (difference > (scalarLimit >> 1)) difference -= scalarLimit;
            }

            int compressed = CompressDifference(difference, tuning.CompressionMode);
            int reconstructedDelta = DecompressDifference(compressed, tuning.CompressionMode);
            estimate = UsesUnclampedTransmitterEstimate(tuning.CompressionMode)
                ? ApplyUpstreamTransmitterDelta(estimate, reconstructedDelta, valueMask, looping)
                : ApplyDeltaToValue(estimate, reconstructedDelta, numBits, looping);
            if (tuning.CorrectEstimateFromValueGray)
            {
                uint estimateGray = ToGrayCode(estimate);
                uint valueGray = ToGrayCode(value);
                for (int i = 0; i < tuning.GrayBits; i++)
                {
                    int correctionBit = (grayBit + i) % numBits;
                    estimateGray = (estimateGray & ~(1u << correctionBit))
                        | (valueGray & (1u << correctionBit));
                }
                estimate = FromGrayCode(estimateGray) & valueMask;
            }
            return estimate;
        }

        public static int MaxEncodedBits(int numBits, in Tuning tuning)
        {
            if (numBits < 1 || numBits > 30) throw new ArgumentOutOfRangeException(nameof(numBits));
            int maxDifference = (1 << numBits) - 1;
            int compressed = CompressDifference(maxDifference, tuning.CompressionMode);
            uint encoded = compressed == 0 ? 1u : (uint)compressed << 1;
            int bits = BitLength(encoded);
            return (bits - 1) + bits + tuning.GrayBits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ToGrayCode(uint value) => value ^ (value >> 1);

        public static uint FromGrayCode(uint gray)
        {
            uint value = 0;
            for (; gray != 0; gray >>= 1) value ^= gray;
            return value;
        }

        private static int CompressDifference(int difference, DifferenceCompressionMode mode)
        {
            if (difference == 0) return 0;
            int sign = difference > 0 ? 1 : -1;
            uint magnitude = (uint)Math.Abs(difference);
            int root;

            switch (mode)
            {
                case DifferenceCompressionMode.UpstreamPow:
                    // Upstream 8676848 adds 0.4 before truncation. This fixes a differential
                    // compression case that could fail to converge while deliberately remaining
                    // distinct from both mathematical floor and nearest-integer cube roots.
                    root = (int)(Math.Pow(magnitude, 0.3333333d) + 0.4d);
                    break;
                case DifferenceCompressionMode.FloorCubeRoot:
                    root = IntegerCubeRoot(magnitude);
                    break;
                case DifferenceCompressionMode.NearestCubeRoot:
                    root = IntegerCubeRoot(magnitude);
                    long lowerError = Math.Abs((long)root * root * root - magnitude);
                    int upper = root + 1;
                    long upperError = Math.Abs((long)upper * upper * upper - magnitude);
                    if (upperError < lowerError) root = upper;
                    break;
                case DifferenceCompressionMode.SquareRoot04:
                    root = (int)(Math.Sqrt(magnitude) + 0.4d);
                    break;
                case DifferenceCompressionMode.NearestSquareRoot:
                    root = (int)Math.Sqrt(magnitude);
                    long lowerSquareError = Math.Abs((long)root * root - magnitude);
                    int upperSquare = root + 1;
                    long upperSquareError = Math.Abs((long)upperSquare * upperSquare - magnitude);
                    if (upperSquareError < lowerSquareError) root = upperSquare;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            return root * sign;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int DecompressDifference(int compressed, DifferenceCompressionMode mode)
        {
            long value;
            if (mode == DifferenceCompressionMode.SquareRoot04
                || mode == DifferenceCompressionMode.NearestSquareRoot)
            {
                long magnitude = Math.Abs((long)compressed);
                value = magnitude * magnitude;
                if (compressed < 0) value = -value;
            }
            else
            {
                value = (long)compressed * compressed * compressed;
            }

            if (value > int.MaxValue) return int.MaxValue;
            if (value < int.MinValue) return int.MinValue;
            return (int)value;
        }

        /// <summary>Floor cube root using integer arithmetic only.</summary>
        private static int IntegerCubeRoot(uint value)
        {
            int low = 0;
            int high = 1;
            while ((long)high * high * high <= value && high < 2048) high <<= 1;
            while (low + 1 < high)
            {
                int middle = low + ((high - low) >> 1);
                long cube = (long)middle * middle * middle;
                if (cube <= value) low = middle;
                else high = middle;
            }
            return low;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool UsesUnclampedTransmitterEstimate(DifferenceCompressionMode mode)
            => mode == DifferenceCompressionMode.UpstreamPow
                || mode == DifferenceCompressionMode.SquareRoot04
                || mode == DifferenceCompressionMode.NearestSquareRoot;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ApplyUpstreamTransmitterDelta(uint value, int delta, uint mask, bool looping)
        {
            // NumerelEncode stores remote_estimate as an unsigned value and only masks it in
            // looping mode. In non-looping mode the lossy cube can temporarily overshoot the
            // nominal scalar range; preserving that state is required for upstream bit parity.
            uint result = unchecked(value + (uint)delta);
            return looping ? result & mask : result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ApplyDeltaToValue(uint value, int delta, int numBits, bool looping)
        {
            uint mask = (1u << numBits) - 1u;
            if (looping) return (uint)((value + (long)delta) & mask);

            long result = value + (long)delta;
            if (result < 0) return 0;
            if (result > mask) return mask;
            return (uint)result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BitLength(uint value)
        {
            int bits = 0;
            do
            {
                bits++;
                value >>= 1;
            }
            while (value != 0);
            return bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBit(byte[] destination, ref int bitPosition, int bit)
        {
            int byteIndex = bitPosition >> 3;
            int bitInByte = 7 - (bitPosition & 7);
            byte mask = (byte)(1 << bitInByte);
            destination[byteIndex] = bit != 0
                ? (byte)(destination[byteIndex] | mask)
                : (byte)(destination[byteIndex] & ~mask);
            bitPosition++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadBit(byte[] source, ref int bitPosition, int bitLimit, out int bit)
        {
            if (bitPosition >= bitLimit)
            {
                bit = 0;
                return false;
            }

            int byteIndex = bitPosition >> 3;
            int bitInByte = 7 - (bitPosition & 7);
            bit = (source[byteIndex] >> bitInByte) & 1;
            bitPosition++;
            return true;
        }
    }
}
