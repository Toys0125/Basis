using Basis.Network.Core.Compression;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Known-answer vectors generated from cnlohr/numerel revision
/// ea184345c109ef1915b1dfe6603d5b188bca8e4e with GCC/glibc on Linux ARM64.
/// The native oracle source and comparison notes live in NUMEREL_BASIS_PORT.md.
/// </summary>
public class BasisNumerelReferenceTests
{
    private static readonly uint[] Values =
    {
        2048, 2056, 2091, 2000, 4095, 0, 1234, 1234, 3000, 2048, 17, 4080,
    };

    private static readonly int[] GrayBits =
    {
        10, 1, 8, 3, 6, 5, 4, 7, 2, 9, 0, 11,
    };

    [Fact]
    public void GrayScramble_MatchesUpstreamVectors()
    {
        Assert.Equal(new[] { 4, 1, 2, 3, 0 }, Scramble(5));
        Assert.Equal(new[] { 6, 1, 4, 3, 2, 5, 0, 7 }, Scramble(8));
        Assert.Equal(new[] { 10, 1, 8, 3, 6, 5, 4, 7, 2, 9, 0, 11 }, Scramble(12));
        Assert.Equal(new[] { 14, 1, 12, 3, 10, 5, 8, 7, 6, 9, 4, 11, 2, 13, 0, 15 }, Scramble(16));
    }

    [Fact]
    public void ReferenceNonLooping_MatchesNativeEncodeDecodeVectors()
    {
        uint[] expectedEstimate =
        {
            2048, 2049, 2076, 2012, 3740, 365, 1094, 1219, 2947, 2218, 21, 3396,
        };
        uint[] expectedBits =
        {
            0xc0000000, 0x40000000, 0x30000000, 0x12000000,
            0x0c400000, 0x0f800000, 0x09000000, 0x15000000,
            0x0c000000, 0x09800000, 0x0dc00000, 0x0f400000,
        };
        int[] expectedLengths = { 2, 4, 6, 8, 10, 10, 10, 8, 10, 10, 10, 10 };
        int[] expectedDelta = { 0, 1, 27, -64, 1728, -3375, 729, 125, 1728, -729, -2197, 3375 };

        RunReferenceSequence(
            looping: false,
            expectedEstimate,
            expectedBits,
            expectedLengths,
            expectedDelta);
    }

    [Fact]
    public void ReferenceLooping_MatchesNativeEncodeDecodeVectors()
    {
        uint[] expectedEstimate =
        {
            2048, 2049, 2076, 2012, 284, 68, 1068, 1193, 2921, 2192, 3920, 4045,
        };
        uint[] expectedBits =
        {
            0xc0000000, 0x40000000, 0x30000000, 0x12000000,
            0x0c800000, 0x1b000000, 0x0a400000, 0x15000000,
            0x0c400000, 0x09800000, 0x0c000000, 0x15000000,
        };
        int[] expectedLengths = { 2, 4, 6, 8, 10, 8, 10, 8, 10, 10, 10, 8 };
        int[] expectedDelta = { 0, 1, 27, -64, -1728, -216, 1000, 125, 1728, -729, 1728, 125 };

        RunReferenceSequence(
            looping: true,
            expectedEstimate,
            expectedBits,
            expectedLengths,
            expectedDelta);
    }

    [Fact]
    public void ReferenceApplyLastDelta_MatchesNativeLossVectors()
    {
        uint[] values = { 2048, 2100, 2200, 2300, 2400, 2500, 2600, 2700 };
        bool[] dropped = { false, false, true, true, false, false, true, false };
        uint[] expectedTx = { 2048, 2075, 2139, 2264, 2389, 2453, 2578, 2642 };
        uint[] expectedRaw = { 2048, 2075, 2102, 2129, 2225, 2289, 2353, 2446 };
        int[] expectedLastDelta = { 0, 27, 27, 27, 125, 64, 64, 64 };
        uint[] expectedOutput = { 2048, 2075, 2102, 2129, 2246, 2304, 2368, 2435 };

        var tx = new BasisNumerel.TxState();
        var rx = new BasisNumerel.RxState();
        tx.Reset(2048);
        rx.Reset(2048);
        var packet = new byte[8];

        for (int frame = 0; frame < values.Length; frame++)
        {
            Array.Fill(packet, (byte)0xa5);
            int write = 0;
            int grayBit = BasisNumerel.GrayScramble(frame % 12, 12);
            Assert.True(BasisNumerel.TryEncode(
                ref tx, values[frame], grayBit, 12, false,
                BasisNumerel.Tuning.Reference, packet, ref write, packet.Length * 8));

            if (dropped[frame])
            {
                BasisNumerel.ApplyLastDelta(ref rx, 12, false);
            }
            else
            {
                int read = 0;
                Assert.True(BasisNumerel.TryDecode(
                    ref rx, grayBit, 12, false,
                    BasisNumerel.Tuning.Reference, packet, ref read, write, out _));
                Assert.Equal(write, read);
            }

            Assert.Equal(expectedTx[frame], tx.RemoteEstimate);
            Assert.Equal(expectedRaw[frame], rx.RawEstimate);
            Assert.Equal(expectedLastDelta[frame], rx.LastDelta);
            Assert.Equal(expectedOutput[frame], rx.OutputValue);
        }
    }

    [Fact]
    public void ReferencePowCubeRoot_MatchesNativeBoundaryVectors()
    {
        int[] magnitudes =
        {
            1, 7, 8, 9, 26, 27, 28, 63, 64, 65, 124, 125, 126,
            215, 216, 217, 342, 343, 344, 511, 512, 513, 728, 729, 730, 999, 1000,
        };
        int[] compressed =
        {
            1, 1, 1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5,
            5, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9,
        };

        var packet = new byte[8];
        for (int i = 0; i < magnitudes.Length; i++)
        {
            var positive = new BasisNumerel.TxState();
            positive.Reset(0);
            int positiveWrite = 0;
            Assert.True(BasisNumerel.TryEncode(
                ref positive, (uint)magnitudes[i], 0, 12, false,
                BasisNumerel.Tuning.Reference, packet, ref positiveWrite, packet.Length * 8));
            Assert.Equal((uint)(compressed[i] * compressed[i] * compressed[i]), positive.RemoteEstimate);

            var negative = new BasisNumerel.TxState();
            negative.Reset(2048);
            int negativeWrite = 0;
            Assert.True(BasisNumerel.TryEncode(
                ref negative, (uint)(2048 - magnitudes[i]), 0, 12, false,
                BasisNumerel.Tuning.Reference, packet, ref negativeWrite, packet.Length * 8));
            Assert.Equal((uint)(2048 - compressed[i] * compressed[i] * compressed[i]), negative.RemoteEstimate);
        }
    }

    [Fact]
    public void ReferencePowCubeRoot_ExhaustiveTwelveBitRangeMatchesNativeChecksum()
    {
        const ulong expectedNativeHash = 0x56c42c8cc4f31f27UL;
        ulong hash = 14695981039346656037UL;

        for (int difference = -4095; difference <= 4095; difference++)
        {
            uint initial = difference < 0 ? 4095u : 0u;
            uint value = (uint)((int)initial + difference);
            uint predicted = BasisNumerel.PredictRemoteEstimate(
                initial,
                value,
                0,
                12,
                false,
                BasisNumerel.Tuning.Reference);
            uint reconstructed = unchecked((uint)((int)predicted - (int)initial));

            for (int byteIndex = 0; byteIndex < 4; byteIndex++)
            {
                hash ^= (reconstructed >> (byteIndex * 8)) & 0xffu;
                hash *= 1099511628211UL;
            }
        }

        Assert.Equal(expectedNativeHash, hash);
    }

    [Fact]
    public void TryDecode_TruncatedCodeIsTransactional()
    {
        var tx = new BasisNumerel.TxState();
        tx.Reset(0);
        var packet = new byte[8];
        int write = 0;
        Assert.True(BasisNumerel.TryEncode(
            ref tx, 343, 0, 12, false,
            BasisNumerel.Tuning.Reference, packet, ref write, packet.Length * 8));

        var rx = new BasisNumerel.RxState();
        rx.Reset(77);
        rx.LastDelta = -8;
        BasisNumerel.RxState before = rx;
        int read = 0;

        Assert.False(BasisNumerel.TryDecode(
            ref rx, 0, 12, false,
            BasisNumerel.Tuning.Reference, packet, ref read, write - 1, out _));

        Assert.Equal(0, read);
        Assert.Equal(before.RawEstimate, rx.RawEstimate);
        Assert.Equal(before.OutputValue, rx.OutputValue);
        Assert.Equal(before.LastDelta, rx.LastDelta);
    }

    [Fact]
    public void TryEncode_ClearsZeroBitsInReusedBuffer()
    {
        var tx = new BasisNumerel.TxState();
        tx.Reset(0);
        var packet = new byte[] { 0xff };
        int write = 0;

        Assert.True(BasisNumerel.TryEncode(
            ref tx, 0, 0, 1, false,
            BasisNumerel.Tuning.Reference, packet, ref write, 8));

        Assert.Equal(2, write);
        Assert.Equal(0xbf, packet[0]); // "10" was written over an all-one byte.
    }

    private static int[] Scramble(int bits)
    {
        var result = new int[bits];
        for (int frame = 0; frame < bits; frame++)
            result[frame] = BasisNumerel.GrayScramble(frame, bits);
        return result;
    }

    private static void RunReferenceSequence(
        bool looping,
        uint[] expectedEstimate,
        uint[] expectedBits,
        int[] expectedLengths,
        int[] expectedDelta)
    {
        var tx = new BasisNumerel.TxState();
        var rx = new BasisNumerel.RxState();
        tx.Reset(2048);
        rx.Reset(2048);
        var packet = new byte[8];

        for (int frame = 0; frame < Values.Length; frame++)
        {
            Array.Fill(packet, (byte)0xa5);
            int write = 0;
            Assert.Equal(GrayBits[frame], BasisNumerel.GrayScramble(frame % 12, 12));
            Assert.True(BasisNumerel.TryEncode(
                ref tx, Values[frame], GrayBits[frame], 12, looping,
                BasisNumerel.Tuning.Reference, packet, ref write, packet.Length * 8));

            Assert.Equal(expectedLengths[frame], write);
            Assert.Equal(expectedBits[frame], ReadTopAligned(packet, write));
            Assert.Equal(expectedEstimate[frame], tx.RemoteEstimate);

            int read = 0;
            Assert.True(BasisNumerel.TryDecode(
                ref rx, GrayBits[frame], 12, looping,
                BasisNumerel.Tuning.Reference, packet, ref read, write, out uint output));

            Assert.Equal(write, read);
            Assert.Equal(expectedEstimate[frame], rx.RawEstimate);
            Assert.Equal(expectedEstimate[frame], output);
            Assert.Equal(expectedEstimate[frame], rx.OutputValue);
            Assert.Equal(expectedDelta[frame], rx.LastDelta);
        }
    }

    private static uint ReadTopAligned(byte[] source, int bitLength)
    {
        uint result = 0;
        for (int bit = 0; bit < bitLength; bit++)
        {
            int value = (source[bit >> 3] >> (7 - (bit & 7))) & 1;
            result |= (uint)value << (31 - bit);
        }
        return result;
    }
}
