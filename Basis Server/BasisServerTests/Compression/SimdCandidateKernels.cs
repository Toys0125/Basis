using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Runtime.InteropServices;
using Basis.Network.Core.Compression;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using static Basis.Network.Core.Compression.BasisAvatarBitPacking;

namespace BasisServerTests.Compression
{
    /// <summary>
    /// Benchmark-only SIMD prototypes. None of these methods are referenced by production code.
    /// </summary>
    internal static class SimdCandidateKernels
    {
        private static readonly int[] HighOffsets = BuildOffsets(BitQuality.High);
        private static readonly int[] MediumOffsets = BuildOffsets(BitQuality.Medium);
        private static readonly int[] LowOffsets = BuildOffsets(BitQuality.Low);
        private static readonly int[] VeryLowOffsets = BuildOffsets(BitQuality.VeryLow);
        private static readonly int PositionBytes = WritePosition;
        private static readonly int HighRotationBytes = BasisBoneRotationCompression.RotationBytes(BitQuality.High);
        private static readonly int MediumRotationBytes = BasisBoneRotationCompression.RotationBytes(BitQuality.Medium);
        private static readonly int LowRotationBytes = BasisBoneRotationCompression.RotationBytes(BitQuality.Low);
        private static readonly int VeryLowRotationBytes = BasisBoneRotationCompression.RotationBytes(BitQuality.VeryLow);
        private static readonly int HighPayloadSize = PositionBytes + HighRotationBytes + BasisBoneRotationCompression.TailBytes;
        private static readonly int MediumPayloadSize = PositionBytes + MediumRotationBytes + BasisBoneRotationCompression.TailBytes;
        private static readonly int LowPayloadSize = PositionBytes + LowRotationBytes + BasisBoneRotationCompression.TailBytes;
        private static readonly int VeryLowPayloadSize = PositionBytes + VeryLowRotationBytes + BasisBoneRotationCompression.TailBytes;

        internal enum BodyRescaleKernel
        {
            PortableVector,
            Avx2
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong ScalarWordDiffMask(byte[] current, byte[] baseline, int length)
        {
            ulong mask = 0;
            int i = 0;
            for (; i + 8 <= length; i += 8)
            {
                ulong a = Unsafe.ReadUnaligned<ulong>(ref current[i]);
                ulong b = Unsafe.ReadUnaligned<ulong>(ref baseline[i]);
                if (a != b) mask |= 1UL << (i >> 3);
            }

            if (i < length)
            {
                for (int k = i; k < length; k++)
                {
                    if (current[k] == baseline[k]) continue;
                    mask |= 1UL << (i >> 3);
                    break;
                }
            }
            return mask;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong Avx2WordDiffMask(byte[] current, byte[] baseline, int length)
        {
            if (!Avx2.IsSupported) return ScalarWordDiffMask(current, baseline, length);

            ulong mask = 0;
            int i = 0;
            for (; i + 32 <= length; i += 32)
            {
                Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ref current[i]);
                Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ref baseline[i]);
                uint equalityMask = (uint)Avx2.MoveMask(Avx2.CompareEqual(a, b));
                uint dirty =
                    ((equalityMask & 0x000000FFu) != 0x000000FFu ? 1u : 0u) |
                    ((equalityMask & 0x0000FF00u) != 0x0000FF00u ? 2u : 0u) |
                    ((equalityMask & 0x00FF0000u) != 0x00FF0000u ? 4u : 0u) |
                    ((equalityMask & 0xFF000000u) != 0xFF000000u ? 8u : 0u);
                mask |= (ulong)dirty << (i >> 3);
            }

            return FinishWordMask(current, baseline, length, i, mask);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong Avx2WordDiffMaskBranchless(byte[] current, byte[] baseline, int length)
        {
            if (!Avx2.IsSupported) return ScalarWordDiffMask(current, baseline, length);

            ulong mask = 0;
            int i = 0;
            for (; i + 32 <= length; i += 32)
            {
                Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ref current[i]);
                Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ref baseline[i]);
                uint diffBits = ~(uint)Avx2.MoveMask(Avx2.CompareEqual(a, b));

                // OR-reduce each independent group of eight byte-difference bits into the low bit
                // of its byte, then pack those four low bits into a nibble with one multiply.
                diffBits |= diffBits >> 4;
                diffBits |= diffBits >> 2;
                diffBits |= diffBits >> 1;
                diffBits &= 0x01010101u;
                uint dirty = (diffBits * 0x01020408u) >> 24;
                mask |= (ulong)dirty << (i >> 3);
            }

            return FinishWordMask(current, baseline, length, i, mask);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong Avx2WordDiffMaskPext(byte[] current, byte[] baseline, int length)
        {
            if (!Avx2.IsSupported || !Bmi2.IsSupported) return ScalarWordDiffMask(current, baseline, length);

            ulong mask = 0;
            int i = 0;
            for (; i + 32 <= length; i += 32)
            {
                Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ref current[i]);
                Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ref baseline[i]);
                uint diffBits = ~(uint)Avx2.MoveMask(Avx2.CompareEqual(a, b));
                diffBits |= diffBits >> 4;
                diffBits |= diffBits >> 2;
                diffBits |= diffBits >> 1;
                uint dirty = Bmi2.ParallelBitExtract(diffBits, 0x01010101u);
                mask |= (ulong)dirty << (i >> 3);
            }

            return FinishWordMask(current, baseline, length, i, mask);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong HybridVectorAvx2WordDiffMask(byte[] current, byte[] baseline, int length)
        {
            if (!Avx2.IsSupported || !Vector.IsHardwareAccelerated)
                return ScalarWordDiffMask(current, baseline, length);

            ulong mask = 0;
            int i = 0;
            int step = Vector<byte>.Count;
            for (; i + step <= length; i += step)
            {
                if (Vector.EqualsAll(new Vector<byte>(current, i), new Vector<byte>(baseline, i)))
                    continue;

                int end = i + step;
                int p = i;
                for (; p + 32 <= end; p += 32)
                {
                    Vector256<byte> a = Unsafe.ReadUnaligned<Vector256<byte>>(ref current[p]);
                    Vector256<byte> b = Unsafe.ReadUnaligned<Vector256<byte>>(ref baseline[p]);
                    uint diffBits = ~(uint)Avx2.MoveMask(Avx2.CompareEqual(a, b));
                    diffBits |= diffBits >> 4;
                    diffBits |= diffBits >> 2;
                    diffBits |= diffBits >> 1;
                    diffBits &= 0x01010101u;
                    uint dirty = (diffBits * 0x01020408u) >> 24;
                    mask |= (ulong)dirty << (p >> 3);
                }
                for (; p + 8 <= end; p += 8)
                {
                    ulong a = Unsafe.ReadUnaligned<ulong>(ref current[p]);
                    ulong b = Unsafe.ReadUnaligned<ulong>(ref baseline[p]);
                    if (a != b) mask |= 1UL << (p >> 3);
                }
            }

            return FinishWordMask(current, baseline, length, i, mask);
        }

        private static ulong FinishWordMask(byte[] current, byte[] baseline, int length, int i, ulong mask)
        {
            for (; i + 8 <= length; i += 8)
            {
                ulong a = Unsafe.ReadUnaligned<ulong>(ref current[i]);
                ulong b = Unsafe.ReadUnaligned<ulong>(ref baseline[i]);
                if (a != b) mask |= 1UL << (i >> 3);
            }

            if (i < length)
            {
                for (int k = i; k < length; k++)
                {
                    if (current[k] == baseline[k]) continue;
                    mask |= 1UL << (i >> 3);
                    break;
                }
            }
            return mask;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Rescale27Scalar(ReadOnlySpan<uint> source, Span<uint> destination, int destinationBits)
        {
            for (int i = 0; i < 27; i++)
                destination[i] = QuantRescaleTable.Rescale(source[i], 12, destinationBits);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Rescale27PortableVector(ReadOnlySpan<uint> source, Span<uint> destination, int destinationBits)
        {
            uint maxDst = (1u << destinationBits) - 1u;
            int width = Vector<uint>.Count;
            int i = 0;
            var vMaxDst = new Vector<uint>(maxDst);
            var vHalf = new Vector<uint>(2047u);
            var vOne = new Vector<uint>(1u);

            for (; i + width <= 27; i += width)
            {
                var q = new Vector<uint>(source.Slice(i, width));
                var num = q * vMaxDst + vHalf;
                var t = num + vOne;
                var result = Vector.ShiftRightLogical(t + Vector.ShiftRightLogical(t, 12), 12);
                result.CopyTo(destination.Slice(i, width));
            }

            for (; i < 27; i++)
                destination[i] = Rescale12ExactFast(source[i], maxDst);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Rescale27Avx2(ReadOnlySpan<uint> source, Span<uint> destination, int destinationBits)
        {
            if (!Avx2.IsSupported)
            {
                Rescale27PortableVector(source, destination, destinationBits);
                return;
            }

            uint maxDst = (1u << destinationBits) - 1u;
            Vector256<int> vMaxDst = Vector256.Create((int)maxDst);
            Vector256<uint> vHalf = Vector256.Create(2047u);
            Vector256<uint> vOne = Vector256.Create(1u);
            ref uint sourceRef = ref MemoryMarshal.GetReference(source);
            ref uint destinationRef = ref MemoryMarshal.GetReference(destination);
            int i = 0;
            for (; i + 8 <= 27; i += 8)
            {
                Vector256<uint> q = Vector256.LoadUnsafe(ref sourceRef, (nuint)i);
                Vector256<uint> product = Avx2.MultiplyLow(q.AsInt32(), vMaxDst).AsUInt32();
                Vector256<uint> num = Avx2.Add(product, vHalf);
                Vector256<uint> t = Avx2.Add(num, vOne);
                Vector256<uint> result = Avx2.ShiftRightLogical(
                    Avx2.Add(t, Avx2.ShiftRightLogical(t, 12)), 12);
                result.StoreUnsafe(ref destinationRef, (nuint)i);
            }

            for (; i < 27; i++)
                destination[i] = Rescale12ExactFast(source[i], maxDst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Rescale12ExactFast(uint qSrc, uint maxDst)
        {
            // Exact divide by 4095 for this bounded numerator:
            // floor(n / (2^12-1)) == (n+1 + ((n+1)>>12)) >> 12.
            uint t = qSrc * maxDst + 2047u + 1u;
            return (t + (t >> 12)) >> 12;
        }

        internal static void BuildAllLowerFromHighIntoBatchedBody(
            in SerializableBasis.LocalAvatarSyncMessage srcHigh,
            ref SerializableBasis.LocalAvatarSyncMessage medium,
            ref SerializableBasis.LocalAvatarSyncMessage low,
            ref SerializableBasis.LocalAvatarSyncMessage veryLow,
            BodyRescaleKernel kernel)
        {
            const int bodyBoneCount = 9;
            int posBytes = PositionBytes;

            if (srcHigh.array == null) throw new ArgumentNullException(nameof(srcHigh.array));
            if (srcHigh.array.Length < HighPayloadSize) throw new ArgumentException("High payload too small", nameof(srcHigh));

            EnsureBuffer(ref medium, BitQuality.Medium, MediumPayloadSize);
            EnsureBuffer(ref low, BitQuality.Low, LowPayloadSize);
            EnsureBuffer(ref veryLow, BitQuality.VeryLow, VeryLowPayloadSize);

            Buffer.BlockCopy(srcHigh.array, 0, medium.array, 0, posBytes);
            Buffer.BlockCopy(srcHigh.array, 0, low.array, 0, posBytes);
            Buffer.BlockCopy(srcHigh.array, 0, veryLow.array, 0, posBytes);
            Array.Clear(medium.array, posBytes, MediumRotationBytes);
            Array.Clear(low.array, posBytes, LowRotationBytes);
            Array.Clear(veryLow.array, posBytes, VeryLowRotationBytes);

            Span<uint> indices = stackalloc uint[bodyBoneCount];
            Span<uint> sourceComponents = stackalloc uint[27];
            Span<uint> medComponents = stackalloc uint[27];
            Span<uint> lowComponents = stackalloc uint[27];
            Span<uint> vlowComponents = stackalloc uint[27];

            for (int slot = 0; slot < bodyBoneCount; slot++)
            {
                int bpcSrc = BasisBoneRotationCompression.BPC_HIGH[slot];
                ulong raw = BasisBitCodec.Read(srcHigh.array, (posBytes << 3) + HighOffsets[slot], 2 + 3 * bpcSrc);
                uint mask = (1u << bpcSrc) - 1u;
                indices[slot] = (uint)(raw & 3UL);
                int component = slot * 3;
                sourceComponents[component] = (uint)((raw >> 2) & mask);
                sourceComponents[component + 1] = (uint)((raw >> (2 + bpcSrc)) & mask);
                sourceComponents[component + 2] = (uint)((raw >> (2 + 2 * bpcSrc)) & mask);
            }

            RescaleBody(sourceComponents, medComponents, 8, kernel);
            RescaleBody(sourceComponents, lowComponents, 6, kernel);
            RescaleBody(sourceComponents, vlowComponents, 5, kernel);

            for (int slot = 0; slot < bodyBoneCount; slot++)
            {
                int component = slot * 3;
                WritePackedBodyBone(medium.array, posBytes, MediumOffsets[slot], 8, indices[slot], medComponents, component);
                WritePackedBodyBone(low.array, posBytes, LowOffsets[slot], 6, indices[slot], lowComponents, component);
                WritePackedBodyBone(veryLow.array, posBytes, VeryLowOffsets[slot], 5, indices[slot], vlowComponents, component);
            }

            for (int slot = bodyBoneCount; slot < BasisBoneRotationCompression.WireBoneSlotCount; slot++)
            {
                RepackRestrictedBone(srcHigh.array, posBytes, HighOffsets, slot, medium.array, MediumOffsets[slot], BitQuality.Medium);
                RepackRestrictedBone(srcHigh.array, posBytes, HighOffsets, slot, low.array, LowOffsets[slot], BitQuality.Low);
                RepackRestrictedBone(srcHigh.array, posBytes, HighOffsets, slot, veryLow.array, VeryLowOffsets[slot], BitQuality.VeryLow);
            }

            int srcCurl = BasisBoneRotationCompression.CurlBits(BitQuality.High);
            int srcSplay = BasisBoneRotationCompression.SplayBits(BitQuality.High);
            for (int finger = 0; finger < BasisBoneRotationCompression.FingerChannelCount; finger++)
            {
                int field = BasisBoneRotationCompression.WireBoneSlotCount + finger;
                int srcBit = (posBytes << 3) + HighOffsets[field];
                uint curl = (uint)BasisBitCodec.Read(srcHigh.array, srcBit, srcCurl);
                uint splay = (uint)BasisBitCodec.Read(srcHigh.array, srcBit + srcCurl, srcSplay);
                RepackFinger(medium.array, posBytes, MediumOffsets[field], BitQuality.Medium, curl, splay, srcCurl, srcSplay);
                RepackFinger(low.array, posBytes, LowOffsets[field], BitQuality.Low, curl, splay, srcCurl, srcSplay);
                RepackFinger(veryLow.array, posBytes, VeryLowOffsets[field], BitQuality.VeryLow, curl, splay, srcCurl, srcSplay);
            }

            int srcTail = posBytes + HighRotationBytes;
            Buffer.BlockCopy(srcHigh.array, srcTail, medium.array, posBytes + MediumRotationBytes, BasisBoneRotationCompression.TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTail, low.array, posBytes + LowRotationBytes, BasisBoneRotationCompression.TailBytes);
            Buffer.BlockCopy(srcHigh.array, srcTail, veryLow.array, posBytes + VeryLowRotationBytes, BasisBoneRotationCompression.TailBytes);
        }

        private static void RescaleBody(ReadOnlySpan<uint> source, Span<uint> destination, int destinationBits, BodyRescaleKernel kernel)
        {
            if (kernel == BodyRescaleKernel.Avx2) Rescale27Avx2(source, destination, destinationBits);
            else Rescale27PortableVector(source, destination, destinationBits);
        }

        private static int[] BuildOffsets(BitQuality quality)
        {
            var offsets = new int[BasisBoneRotationCompression.RotationFieldCount];
            BasisBoneRotationCompression.BuildRotationFieldOffsets(quality, offsets);
            return offsets;
        }

        private static void WritePackedBodyBone(byte[] destination, int baseByteOffset, int bitOffset, int bpcDst,
            uint index, ReadOnlySpan<uint> components, int component)
        {
            ulong packed = index
                | ((ulong)components[component] << 2)
                | ((ulong)components[component + 1] << (2 + bpcDst))
                | ((ulong)components[component + 2] << (2 + 2 * bpcDst));
            BasisBitCodec.Or(destination, (baseByteOffset << 3) + bitOffset, packed, 2 + 3 * bpcDst);
        }

        private static void RepackRestrictedBone(byte[] source, int sourceBase, int[] highOffsets, int slot,
            byte[] destination, int destinationBitOffset, BitQuality quality)
        {
            int srcBit = (sourceBase << 3) + highOffsets[slot];
            if (BasisBoneRotationCompression.BONE_DOF[slot] == 1)
            {
                int srcBits = BasisBoneRotationCompression.SingleAxisBits(BitQuality.High);
                int dstBits = BasisBoneRotationCompression.SingleAxisBits(quality);
                uint value = (uint)BasisBitCodec.Read(source, srcBit, srcBits);
                BasisBitCodec.Or(destination, (sourceBase << 3) + destinationBitOffset,
                    QuantRescaleTable.Rescale(value, srcBits, dstBits), dstBits);
                return;
            }

            int srcHinge = BasisBoneRotationCompression.HingeBits(BitQuality.High);
            int srcTwist = BasisBoneRotationCompression.TwistBits(BitQuality.High);
            int dstHinge = BasisBoneRotationCompression.HingeBits(quality);
            int dstTwist = BasisBoneRotationCompression.TwistBits(quality);
            uint hinge = (uint)BasisBitCodec.Read(source, srcBit, srcHinge);
            uint twist = (uint)BasisBitCodec.Read(source, srcBit + srcHinge, srcTwist);
            ulong packed = QuantRescaleTable.Rescale(hinge, srcHinge, dstHinge)
                | ((ulong)QuantRescaleTable.Rescale(twist, srcTwist, dstTwist) << dstHinge);
            BasisBitCodec.Or(destination, (sourceBase << 3) + destinationBitOffset, packed, dstHinge + dstTwist);
        }

        private static void RepackFinger(byte[] destination, int baseByteOffset, int bitOffset, BitQuality quality,
            uint curl, uint splay, int srcCurlBits, int srcSplayBits)
        {
            int dstCurl = BasisBoneRotationCompression.CurlBits(quality);
            int dstSplay = BasisBoneRotationCompression.SplayBits(quality);
            ulong packed = QuantRescaleTable.Rescale(curl, srcCurlBits, dstCurl)
                | ((ulong)QuantRescaleTable.Rescale(splay, srcSplayBits, dstSplay) << dstCurl);
            BasisBitCodec.Or(destination, (baseByteOffset << 3) + bitOffset, packed, dstCurl + dstSplay);
        }

        private static void EnsureBuffer(ref SerializableBasis.LocalAvatarSyncMessage message, BitQuality quality, int size)
        {
            message.DataQualityLevel = (byte)quality;
            if (message.array != null && message.array.Length >= size) return;
            message.array = new byte[size];
        }
    }
}
