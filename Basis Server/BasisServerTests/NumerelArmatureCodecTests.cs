using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

public class NumerelArmatureCodecTests
{
    [Fact]
    public void Scalar_StaticValue_RoundTripsAndConverges()
    {
        var tuning = BasisNumerel.Tuning.ArmaturePoc;
        var tx = new BasisNumerel.TxState();
        var rx = new BasisNumerel.RxState();
        tx.Reset(2048);
        rx.Reset(2048);
        var packet = new byte[32];
        uint decoded = 0;

        for (int frame = 0; frame < 32; frame++)
        {
            Array.Clear(packet);
            int write = 0;
            Assert.True(BasisNumerel.TryEncode(ref tx, 3571, frame % 12, 12, false, tuning, packet, ref write, packet.Length * 8));
            int read = 0;
            Assert.True(BasisNumerel.TryDecode(ref rx, frame % 12, 12, false, tuning, packet, ref read, write, out decoded));
            Assert.Equal(write, read);
        }

        Assert.Equal(3571u, tx.RemoteEstimate);
        Assert.Equal(3571u, decoded);
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void Armature_StreamDecodesAndPreservesAbsoluteFields(BitQuality quality)
    {
        var tuning = BasisNumerel.Tuning.ArmaturePoc;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, tuning);
        var decoder = new BasisNumerelArmatureCodec.Decoder(quality, tuning);
        var rng = new Random(17 + (int)quality);
        byte[] source = S.MakeRealisticPayload(quality, rng);
        byte[] body = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        for (int frame = 0; frame < 40; frame++)
        {
            int len = encoder.Encode(source, (byte)frame, body, 0);
            Assert.InRange(len, 1, encoder.MaxBodySize);
            Assert.True(decoder.TryDecode(body, 0, len, (byte)frame, output, out int consumed));
            Assert.Equal(len, consumed);
        }

        Assert.Equal(source.AsSpan(0, S.PosBytes(quality)).ToArray(), output.AsSpan(0, S.PosBytes(quality)).ToArray());
        int tail = S.TailStart(quality);
        Assert.Equal(source.AsSpan(tail, source.Length - tail).ToArray(), output.AsSpan(tail, output.Length - tail).ToArray());
        for (int bone = 0; bone < S.BoneCount; bone++)
            Assert.Equal(S.GetBone(source, quality, bone), S.GetBone(output, quality, bone));
    }

    [Fact]
    public void LateJoin_StaticPose_ConvergesWithoutKeyframeUnderLoss()
    {
        const BitQuality quality = BitQuality.High;
        var tuning = BasisNumerel.Tuning.ArmaturePoc;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, tuning);
        var lateDecoder = new BasisNumerelArmatureCodec.Decoder(quality, tuning);
        var rng = new Random(991);
        byte[] source = S.MakeRealisticPayload(quality, rng);
        byte[] body = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        // Advance the global stream before this receiver exists.
        for (int frame = 0; frame < 80; frame++)
            Assert.True(encoder.Encode(source, (byte)frame, body, 0) > 0);

        int delivered = 0;
        for (int frame = 80; frame < 160; frame++)
        {
            int len = encoder.Encode(source, (byte)frame, body, 0);
            Assert.True(len > 0);
            // Deterministic 25% loss. Sequence gaps are intentional.
            if ((frame & 3) == 1) continue;
            Assert.True(lateDecoder.TryDecode(body, 0, len, (byte)frame, output, out _));
            delivered++;
        }

        Assert.True(delivered >= 50);
        for (int bone = 0; bone < S.BoneCount; bone++)
            Assert.Equal(S.GetBone(source, quality, bone), S.GetBone(output, quality, bone));
    }

    [Fact]
    public void DuplicateAndReorderedPackets_AreRejectedWithoutPoisoningState()
    {
        const BitQuality quality = BitQuality.High;
        var tuning = BasisNumerel.Tuning.ArmaturePoc;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, tuning);
        var decoder = new BasisNumerelArmatureCodec.Decoder(quality, tuning);
        var rng = new Random(44);
        byte[] pose = S.MakeRealisticPayload(quality, rng);
        byte[] a = new byte[encoder.MaxBodySize];
        byte[] b = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        int aLen = encoder.Encode(pose, 10, a, 0);
        int bLen = encoder.Encode(pose, 11, b, 0);
        Assert.True(decoder.TryDecode(a, 0, aLen, 10, output, out _));
        Assert.True(decoder.TryDecode(b, 0, bLen, 11, output, out _));
        Assert.False(decoder.TryDecode(a, 0, aLen, 10, output, out _));
        Assert.False(decoder.TryDecode(b, 0, bLen, 11, output, out _));

        byte[] c = new byte[encoder.MaxBodySize];
        int cLen = encoder.Encode(pose, 12, c, 0);
        Assert.True(decoder.TryDecode(c, 0, cLen, 12, output, out _));
    }

    [Fact]
    public void HybridV2_LowBpcBonesRecoverExactlyByRollingRefreshAfterPacketLoss()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelArmatureCodec.Options.HybridV2;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelArmatureCodec.Decoder(quality, options);
        var rng = new Random(20260807);
        byte[] first = S.MakeRealisticPayload(quality, rng);
        byte[] dropped = S.MakeRealisticPayload(quality, rng);
        byte[] current = S.MakeRealisticPayload(quality, rng);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];
        byte[] bpc = S.Bpc(quality);

        // Seven refresh-8 frames cover all 51 bones once and establish a valid exact-delta base.
        for (byte sequence = 0; sequence < 7; sequence++)
        {
            int length = encoder.Encode(first, sequence, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, sequence, output, out _));
        }
        for (int bone = 0; bone < bpc.Length; bone++)
        {
            if (bpc[bone] <= options.ExactDeltaMaxBits)
                Assert.Equal(S.GetBone(first, quality, bone), S.GetBone(output, quality, bone));
        }

        // Sequence 7 is intentionally lost, invalidating the receiver's exact-delta bases.
        Assert.True(encoder.Encode(dropped, 7, packet, 0) > 0);

        // Another full rolling-refresh cycle must re-establish every low-BPC bone exactly even
        // though the intervening temporal deltas were decoded against an intentionally stale base.
        for (byte sequence = 8; sequence < 15; sequence++)
        {
            int length = encoder.Encode(current, sequence, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, sequence, output, out _));
        }
        for (int bone = 0; bone < bpc.Length; bone++)
        {
            if (bpc[bone] <= options.ExactDeltaMaxBits)
                Assert.Equal(S.GetBone(current, quality, bone), S.GetBone(output, quality, bone));
        }
    }

    [Fact]
    public void HybridV2_StaleExactDeltaAtRangeBoundaryDoesNotRejectPacket()
    {
        const BitQuality quality = BitQuality.High;
        const int bone = 19; // first 5-BPC toe bone
        var options = BasisNumerelArmatureCodec.Options.HybridV2;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelArmatureCodec.Decoder(quality, options);
        var rng = new Random(1905);
        byte[] pose = S.MakeRealisticPayload(quality, rng);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        byte componentBits = S.Bpc(quality)[bone];
        ulong componentMask = (1UL << componentBits) - 1UL;
        ulong packed = S.GetBone(pose, quality, bone);
        ulong cleared = packed & ~(componentMask << 2);

        int length0 = encoder.Encode(pose, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length0, 0, output, out _));

        // Sequence 1 refreshes slots 12..23 absolutely with Hybrid V2's refresh-12 schedule.
        S.SetBone(pose, quality, bone, cleared);
        int length1 = encoder.Encode(pose, 1, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length1, 1, output, out _));

        // The sender moves 0 -> max in sequence 2, but that packet is lost. Sequence 3 then
        // carries an exact -1 delta. Applied to the receiver's stale zero base it would become
        // -1; V2 must clamp that loss-induced stale-base prediction rather than reject the body.
        S.SetBone(pose, quality, bone, cleared | (componentMask << 2));
        Assert.True(encoder.Encode(pose, 2, packet, 0) > 0); // dropped
        S.SetBone(pose, quality, bone, cleared | ((componentMask - 1) << 2));
        int length3 = encoder.Encode(pose, 3, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length3, 3, output, out _));
        Assert.Equal((byte)3, decoder.LastSequence);
    }

    [Fact]
    public void HybridV2_OrdinaryGapDoesNotGloballyFreezeTrustedBones()
    {
        const BitQuality quality = BitQuality.High;
        var options = new BasisNumerelArmatureCodec.Options(
            new BasisNumerel.Tuning(1, -1, true, false),
            boneAbsoluteEscape: true,
            residualDivisor: 256,
            refreshBonesPerFrame: 1,
            exactDeltaMaxBits: 6,
            maxPredictionAge: 8,
            refreshNumerelBonesOnly: true);
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelArmatureCodec.Decoder(quality, options);
        var rng = new Random(84);
        byte[] pose = S.MakeRealisticPayload(quality, rng);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        // High-quality bone 0 is the first eligible Numerel bone, so sequence 0 refreshes it
        // absolutely and establishes a trusted displayed value.
        int initialLength = encoder.Encode(pose, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, initialLength, 0, output, out _));
        ulong before = S.GetBone(output, quality, 0);

        byte componentBits = S.Bpc(quality)[0];
        ulong componentMask = (1UL << componentBits) - 1UL;
        ulong packed = S.GetBone(pose, quality, 0);
        ulong a = (packed >> 2) & componentMask;
        ulong a1 = a < componentMask - 2 ? a + 1 : a - 1;
        ulong a2 = a < componentMask - 2 ? a + 2 : a - 2;
        ulong cleared = packed & ~(componentMask << 2);

        S.SetBone(pose, quality, 0, cleared | (a1 << 2));
        Assert.True(encoder.Encode(pose, 1, packet, 0) > 0); // dropped

        S.SetBone(pose, quality, 0, cleared | (a2 << 2));
        int recoveryLength = encoder.Encode(pose, 2, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, recoveryLength, 2, output, out _));

        // First-generation HybridPoc would hold the entire previously displayed skeleton on
        // this gap. V2 keeps the trusted prediction visible; bone 0 advances even though its
        // hidden state is temporarily one exact code point behind the sender.
        Assert.NotEqual(before, S.GetBone(output, quality, 0));
    }

    [Fact]
    public void HybridTruncatedPacket_DoesNotCommitPoseOrValidityState()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelArmatureCodec.Options.HybridPoc;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, options);
        var underTest = new BasisNumerelArmatureCodec.Decoder(quality, options);
        var control = new BasisNumerelArmatureCodec.Decoder(quality, options);
        var rng = new Random(812);
        byte[] pose = S.MakeRealisticPayload(quality, rng);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] underTestOutput = new byte[S.PayloadSize(quality)];
        byte[] controlOutput = new byte[S.PayloadSize(quality)];

        int initialLength = encoder.Encode(pose, 1, packet, 0);
        Assert.True(underTest.TryDecode(packet, 0, initialLength, 1, underTestOutput, out _));
        Assert.True(control.TryDecode(packet, 0, initialLength, 1, controlOutput, out _));
        Assert.Equal(controlOutput, underTestOutput);

        // Sequence 2 schedules absolute refreshes. Truncating only the absolute tail means
        // the decoder has already parsed and tentatively changed armature validity and pose.
        S.FlipBone(pose, quality, 3);
        int truncatedLength = encoder.Encode(pose, 2, packet, 0);
        byte[] outputBeforeFailure = underTestOutput.ToArray();
        Assert.False(underTest.TryDecode(packet, 0, truncatedLength - 1, 2, underTestOutput, out _));
        Assert.Equal((byte)1, underTest.LastSequence);
        Assert.Equal(outputBeforeFailure, underTestOutput);

        // Both decoders intentionally miss sequence 2. They must produce identical sequence-3
        // output; otherwise the failed packet committed hidden state in the decoder under test.
        S.FlipBone(pose, quality, 7);
        int recoveryLength = encoder.Encode(pose, 3, packet, 0);
        Assert.True(underTest.TryDecode(packet, 0, recoveryLength, 3, underTestOutput, out _));
        Assert.True(control.TryDecode(packet, 0, recoveryLength, 3, controlOutput, out _));
        Assert.Equal(controlOutput, underTestOutput);
    }

    [Fact]
    public void TruncatedPacket_DoesNotAdvanceDecoder()
    {
        const BitQuality quality = BitQuality.High;
        var tuning = BasisNumerel.Tuning.ArmaturePoc;
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, tuning);
        var decoder = new BasisNumerelArmatureCodec.Decoder(quality, tuning);
        var rng = new Random(61);
        byte[] pose = S.MakeRealisticPayload(quality, rng);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        int len0 = encoder.Encode(pose, 1, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, len0, 1, output, out _));

        S.FlipBone(pose, quality, 3);
        int len1 = encoder.Encode(pose, 2, packet, 0);
        Assert.False(decoder.TryDecode(packet, 0, len1 - 1, 2, output, out _));
        Assert.Equal((byte)1, decoder.LastSequence);

        Assert.True(decoder.TryDecode(packet, 0, len1, 2, output, out _));
        Assert.Equal((byte)2, decoder.LastSequence);
    }
}
