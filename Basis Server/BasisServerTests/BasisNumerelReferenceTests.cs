using Basis.Network.Core.Compression;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Known-answer vectors generated from cnlohr/numerel revision
/// 8676848ae268f3a8eee672413f272ee422521d09 with GCC/glibc on Linux ARM64.
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
        uint[] expectedTx =
        {
            2048, 2056, 2083, 2019, 4216, 120, 1120, 1245, 2973, 1973, 245, 4341,
        };
        uint[] expectedRaw =
        {
            2048, 2056, 2083, 2019, 3968, 0, 1000, 1178, 2909, 1909, 181, 4095,
        };
        uint[] expectedOutput =
        {
            2048, 2056, 2083, 2019, 4063, 0, 1000, 1138, 2876, 1884, 162, 4095,
        };
        uint[] expectedBits =
        {
            0xc0000000, 0x20000000, 0x30000000, 0x12000000,
            0x0d400000, 0x04200000, 0x0a400000, 0x15000000,
            0x0c000000, 0x0a800000, 0x0cc00000, 0x04100000,
        };
        int[] expectedLengths = { 2, 6, 6, 8, 10, 12, 10, 8, 10, 10, 10, 12 };
        int[] expectedDelta = { 0, 8, 27, -64, 2197, -4096, 1000, 125, 1728, -1000, -1728, 4096 };

        RunReferenceSequence(
            looping: false,
            expectedTx,
            expectedRaw,
            expectedOutput,
            expectedBits,
            expectedLengths,
            expectedDelta);
    }

    [Fact]
    public void ReferenceLooping_MatchesNativeEncodeDecodeVectors()
    {
        uint[] expectedEstimate =
        {
            2048, 2056, 2083, 2019, 3918, 38, 1369, 1244, 2972, 1972, 244, 28,
        };
        uint[] expectedBits =
        {
            0xc0000000, 0x20000000, 0x30000000, 0x12000000,
            0x0dc00000, 0x19000000, 0x0b400000, 0x17000000,
            0x0c000000, 0x0a800000, 0x0c800000, 0x1a000000,
        };
        int[] expectedLengths = { 2, 6, 6, 8, 10, 8, 10, 8, 10, 10, 10, 8 };
        int[] expectedDelta = { 0, 8, 27, -64, -2197, 216, 1331, -125, 1728, -1000, -1728, -216 };

        RunReferenceSequence(
            looping: true,
            expectedEstimate,
            expectedEstimate,
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
        uint[] expectedTx = { 2048, 2112, 2176, 2301, 2426, 2490, 2615, 2679 };
        uint[] expectedRaw = { 2048, 2112, 2176, 2240, 2370, 2493, 2557, 2621 };
        int[] expectedLastDelta = { 0, 64, 64, 64, 125, 64, 64, 64 };
        uint[] expectedOutput = { 2048, 2112, 2176, 2240, 2366, 2445, 2509, 2585 };

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
            1, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 5,
            6, 6, 6, 7, 7, 7, 8, 8, 8, 9, 9, 9, 10, 10,
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
        const ulong expectedNativeHash = 0xb0e90ea47c60370fUL;
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

    [Theory]
    [InlineData(BasisNumerel.DifferenceCompressionMode.SquareRoot04)]
    [InlineData(BasisNumerel.DifferenceCompressionMode.NearestSquareRoot)]
    public void SquareRootModes_ReconstructSignedSquareDeltas(BasisNumerel.DifferenceCompressionMode mode)
    {
        var tuning = new BasisNumerel.Tuning(1, 2, mode, false);
        var packet = new byte[16];

        foreach ((uint initial, uint value) in new[] { (1000u, 1120u), (2000u, 1880u) })
        {
            var tx = new BasisNumerel.TxState();
            var rx = new BasisNumerel.RxState();
            tx.Reset(initial);
            rx.Reset(initial);

            int write = 0;
            Assert.True(BasisNumerel.TryEncode(
                ref tx, value, 0, 12, false, tuning, packet, ref write, packet.Length * 8));

            int read = 0;
            Assert.True(BasisNumerel.TryDecode(
                ref rx, 0, 12, false, tuning, packet, ref read, write, out _));
            Assert.Equal(write, read);

            int magnitude = Math.Abs(rx.LastDelta);
            int root = (int)Math.Sqrt(magnitude);
            Assert.Equal(magnitude, root * root);
            Assert.Equal(initial < value ? 1 : -1, Math.Sign(rx.LastDelta));
        }
    }

    [Theory]
    [InlineData(BasisNumerel.DifferenceCompressionMode.SquareRoot04)]
    [InlineData(BasisNumerel.DifferenceCompressionMode.NearestSquareRoot)]
    public void SquareRootModes_AlternatingExtremesStayWithinAdvertisedBitBudget(BasisNumerel.DifferenceCompressionMode mode)
    {
        var tuning = new BasisNumerel.Tuning(1, 2, mode, false);
        var tx = new BasisNumerel.TxState();
        var rx = new BasisNumerel.RxState();
        tx.Reset(2048);
        rx.Reset(2048);
        int maxBits = BasisNumerel.MaxEncodedBits(12, tuning);
        var packet = new byte[16];

        for (int frame = 0; frame < 512; frame++)
        {
            uint value = (frame & 1) == 0 ? 0u : 4095u;
            int grayBit = BasisNumerel.GrayScramble(frame % 12, 12);
            int write = 0;
            Assert.True(BasisNumerel.TryEncode(
                ref tx, value, grayBit, 12, false, tuning, packet, ref write, packet.Length * 8));
            Assert.InRange(write, 1, maxBits);

            int read = 0;
            Assert.True(BasisNumerel.TryDecode(
                ref rx, grayBit, 12, false, tuning, packet, ref read, write, out _));
            Assert.Equal(write, read);
        }
    }

    public static IEnumerable<object[]> FixedPowerCurves()
    {
        yield return new object[] { BasisNumerel.Tuning.Power1Reference };
        yield return new object[] { BasisNumerel.Tuning.Power1_5Reference };
        yield return new object[] { BasisNumerel.Tuning.Power2Reference };
        yield return new object[] { BasisNumerel.Tuning.Power2_5Reference };
        yield return new object[] { BasisNumerel.Tuning.Power3Reference };
        yield return new object[] { BasisNumerel.Tuning.Power4Reference };
        yield return new object[] { BasisNumerel.Tuning.Power5Reference };
    }

    [Theory]
    [MemberData(nameof(FixedPowerCurves))]
    public void FixedPowerCurves_EncodeDecode16BitExtremes(BasisNumerel.Tuning tuning)
    {
        uint[] values = { 32768, 32769, 33000, 40000, 65535, 0, 12345, 50000, 32768 };
        var tx = new BasisNumerel.TxState();
        var rx = new BasisNumerel.RxState();
        tx.Reset(32768);
        rx.Reset(32768);
        byte[] packet = new byte[16];

        for (int frame = 0; frame < values.Length; frame++)
        {
            int grayBit = BasisNumerel.GrayScramble(frame % 16, 16);
            int write = 0;
            Assert.True(BasisNumerel.TryEncode(ref tx, values[frame], grayBit, 16, false,
                tuning, packet, ref write, packet.Length * 8));
            Assert.InRange(write, 1, BasisNumerel.MaxEncodedBits(16, tuning));

            int read = 0;
            Assert.True(BasisNumerel.TryDecode(ref rx, grayBit, 16, false,
                tuning, packet, ref read, write, out _));
            Assert.Equal(write, read);
            Assert.InRange(rx.RawEstimate, 0u, 65535u);
        }
    }

    [Fact]
    public void Power1Curve_ReconstructsExactDelta()
    {
        var tuning = BasisNumerel.Tuning.Power1Reference;
        foreach (int difference in new[] { -32768, -4095, -17, -1, 0, 1, 17, 4095, 32767 })
        {
            uint initial = difference < 0 ? 32768u : 0u;
            uint value = (uint)((int)initial + difference);
            uint predicted = BasisNumerel.PredictRemoteEstimate(initial, value, 0, 16, false, tuning);
            Assert.Equal(value, predicted);
        }
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
        uint[] expectedTx,
        uint[] expectedRaw,
        uint[] expectedOutput,
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
            Assert.Equal(expectedTx[frame], tx.RemoteEstimate);

            int read = 0;
            Assert.True(BasisNumerel.TryDecode(
                ref rx, GrayBits[frame], 12, looping,
                BasisNumerel.Tuning.Reference, packet, ref read, write, out uint output));

            Assert.Equal(write, read);
            Assert.Equal(expectedRaw[frame], rx.RawEstimate);
            Assert.Equal(expectedOutput[frame], output);
            Assert.Equal(expectedOutput[frame], rx.OutputValue);
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
