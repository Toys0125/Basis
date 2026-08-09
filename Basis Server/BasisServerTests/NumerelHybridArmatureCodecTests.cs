using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

public class NumerelHybridArmatureCodecTests
{
    [Fact]
    public void AuxiliaryBodyLength_InvalidQuality_IsRejected()
    {
        Assert.False(BasisAvatarAuxiliaryDeltaCodec.TryGetBodyLength(
            new byte[] { 0 }, 0, 1, (BitQuality)255, out _, out _));
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void AuxiliaryDelta_IsBaselineRelativeAndLossIndependent(BitQuality quality)
    {
        var encoder = new BasisAvatarAuxiliaryDeltaCodec.Encoder(quality);
        var decoder = new BasisAvatarAuxiliaryDeltaCodec.Decoder(quality);
        var rng = new Random(812000 + (int)quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[S.PayloadSize(quality)];

        byte[] baseline = S.MakeRealisticPayload(quality, rng);
        int length = encoder.Encode(baseline, forceBootstrap: true, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, output, out int consumed));
        Assert.Equal(length, consumed);
        AssertAuxiliaryEqual(baseline, output, quality);

        // Encode and lose a complete auxiliary update. The following delta is still relative to
        // the bootstrap baseline rather than the lost packet, so it reconstructs independently.
        byte[] lost = S.MakeRealisticPayload(quality, rng);
        Assert.True(encoder.Encode(lost, forceBootstrap: false, packet, 0) > 0);

        byte[] current = S.MakeRealisticPayload(quality, rng);
        length = encoder.Encode(current, forceBootstrap: false, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, output, out consumed));
        Assert.Equal(length, consumed);
        AssertAuxiliaryEqual(current, output, quality);
    }

    [Fact]
    public void Bootstrap_SeedsAllRotationGroupsAndRoundTripsExactPackedPose()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelHybridArmatureCodec.Encoder(quality);
        var decoder = new BasisNumerelHybridArmatureCodec.Decoder(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];
        byte[] pose = S.MakeRealisticPayload(quality, new Random(812101));

        int length = encoder.Encode(pose, 0, packet, 0);
        Assert.True(length > 0);
        Assert.True(encoder.LastWasBootstrap);
        Assert.Equal(BasisNumerelHybridArmatureCodec.AllRecoveryGroupsMask, encoder.LastRefreshMask);
        Assert.True(encoder.LastRefreshBytes > 0);
        Assert.True(encoder.LastRotationBytes > 0);
        Assert.True(encoder.LastAuxiliaryBytes > 0);

        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out int consumed));
        Assert.Equal(length, consumed);
        Assert.True(decoder.LastWasBootstrap);
        Assert.True(decoder.IsFullySynchronized);
        Assert.Equal(pose, output);
    }

    [Fact]
    public void LostNormalFrame_InvalidatesAllGroups_AndPassiveCycleRestoresValidity()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelHybridArmatureCodec.Options.PassiveG8C12;
        var encoder = new BasisNumerelHybridArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelHybridArmatureCodec.Decoder(quality, options);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        byte[] pose0 = MakePose(quality, 0);
        int length = encoder.Encode(pose0, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out _));
        Assert.True(decoder.IsFullySynchronized);

        // Sequence 1 advances every shared Numerel predictor but is lost.
        Assert.True(encoder.Encode(MakePose(quality, 1), 1, packet, 0) > 0);

        length = encoder.Encode(MakePose(quality, 2), 2, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 2, output, out _));
        Assert.False(decoder.IsFullySynchronized);
        Assert.NotEqual(BasisNumerelHybridArmatureCodec.AllRecoveryGroupsMask, decoder.ValidGroupMask);

        bool recovered = false;
        for (int frame = 3; frame <= 26; frame++)
        {
            length = encoder.Encode(MakePose(quality, frame), (byte)frame, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out _));
            if (decoder.IsFullySynchronized)
            {
                recovered = true;
                break;
            }
        }
        Assert.True(recovered, "g8/c12 passive refresh did not revalidate all groups within two complete cycles");
    }

    [Fact]
    public void DeadlinePrediction_InvalidatesGroups_HoldsAuxiliary_AndRejectsLatePacket()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelHybridArmatureCodec.Encoder(quality);
        var decoder = new BasisNumerelHybridArmatureCodec.Decoder(quality);
        byte[] packet0 = new byte[encoder.MaxBodySize];
        byte[] packet1 = new byte[encoder.MaxBodySize];
        byte[] packet2 = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        byte[] pose0 = MakePose(quality, 0);
        byte[] pose1 = MakePose(quality, 1);
        byte[] pose2 = MakePose(quality, 2);
        pose0[0] = 0x31;
        pose1[0] = 0x42;
        pose2[0] = 0x53;

        int len0 = encoder.Encode(pose0, 0, packet0, 0);
        int len1 = encoder.Encode(pose1, 1, packet1, 0);
        int len2 = encoder.Encode(pose2, 2, packet2, 0);
        Assert.True(decoder.TryDecode(packet0, 0, len0, 0, output, out _));
        Assert.True(decoder.IsFullySynchronized);

        Assert.True(decoder.TryAdvanceDeadline(1, output));
        Assert.Equal((byte)1, decoder.LastSequence);
        Assert.Equal((byte)0, decoder.ValidGroupMask);
        Assert.Equal(pose0[0], output[0]); // exact auxiliary state is held during prediction

        Assert.False(decoder.TryDecode(packet1, 0, len1, 1, output, out _));
        Assert.Equal((byte)1, decoder.LastSequence);

        Assert.True(decoder.TryDecode(packet2, 0, len2, 2, output, out _));
        Assert.Equal((byte)2, decoder.LastSequence);
        Assert.Equal(pose2[0], output[0]);
    }

    [Fact]
    public void TruncatedPassiveRefresh_IsRejectedBeforeSequenceOrPredictorCommit()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelHybridArmatureCodec.Options.PassiveG8C12;
        var encoder = new BasisNumerelHybridArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelHybridArmatureCodec.Decoder(quality, options);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        int length = encoder.Encode(MakePose(quality, 0), 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out _));

        int targetSequence = -1;
        for (int frame = 1; frame < 20; frame++)
        {
            length = encoder.Encode(MakePose(quality, frame), (byte)frame, packet, 0);
            if (encoder.LastRefreshMask != 0)
            {
                targetSequence = frame;
                break;
            }
        }
        Assert.True(targetSequence > 0);
        Assert.True(encoder.LastRefreshBytes > 0);
        Assert.True(length > 1);

        Assert.False(decoder.TryDecode(packet, 0, length - 1, (byte)targetSequence, output, out _));
        Assert.Equal((byte)0, decoder.LastSequence);
        Assert.True(decoder.IsFullySynchronized);

        Assert.True(decoder.TryDecode(packet, 0, length, (byte)targetSequence, output, out int consumed));
        Assert.Equal(length, consumed);
        Assert.Equal((byte)targetSequence, decoder.LastSequence);
    }

    [Fact]
    public void ZeroBoneRle_DoesNotSuppressScheduledAbsoluteRefresh()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelHybridArmatureCodec.Encoder(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] pose = MakeIdentityPayload(quality);

        bool sawCombinedFrame = false;
        for (int frame = 0; frame < 80; frame++)
        {
            int length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(length > 0);
            if (!encoder.LastWasBootstrap
                && encoder.LastUsedZeroBoneRle
                && encoder.LastRefreshMask != 0)
            {
                Assert.True(encoder.LastRefreshBytes > 0);
                Assert.True(encoder.LastZeroBoneCount > 0);
                sawCombinedFrame = true;
                break;
            }
        }
        Assert.True(sawCombinedFrame, "never observed a scheduled passive refresh on an RLE-selected frame");
    }

    [Fact]
    public void RequestedLateJoinBootstrap_SeedsArbitrarySequenceExactly()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelHybridArmatureCodec.Encoder(quality);
        var decoder = new BasisNumerelHybridArmatureCodec.Decoder(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        // Advance the sender without this receiver.
        for (int frame = 0; frame < 20; frame++)
            Assert.True(encoder.Encode(MakePose(quality, frame), (byte)frame, packet, 0) > 0);

        encoder.RequestBootstrap();
        byte[] joinPose = MakePose(quality, 20);
        int length = encoder.Encode(joinPose, 20, packet, 0);
        Assert.True(encoder.LastWasBootstrap);
        Assert.Equal(BasisNumerelHybridArmatureCodec.AllRecoveryGroupsMask, encoder.LastRefreshMask);

        Assert.True(decoder.TryDecode(packet, 0, length, 20, output, out int consumed));
        Assert.Equal(length, consumed);
        Assert.True(decoder.IsFullySynchronized);
        Assert.Equal(joinPose, output);
    }

    [Fact]
    public void SeparatedSuffix_RleRoundTripsLikePower2Control_WithExactAuxiliary()
    {
        const BitQuality quality = BitQuality.High;
        var controlEncoder = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(
            quality, BasisNumerelSeparatedSuffixArmatureCodec.Options.Power2);
        var rleEncoder = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(
            quality, BasisNumerelSeparatedSuffixArmatureCodec.Options.Power2RleGray);
        var controlDecoder = new BasisNumerelSeparatedSuffixArmatureCodec.Decoder(
            quality, BasisNumerelSeparatedSuffixArmatureCodec.Options.Power2);
        var rleDecoder = new BasisNumerelSeparatedSuffixArmatureCodec.Decoder(
            quality, BasisNumerelSeparatedSuffixArmatureCodec.Options.Power2RleGray);
        byte[] controlPacket = new byte[controlEncoder.MaxBodySize];
        byte[] rlePacket = new byte[rleEncoder.MaxBodySize];
        byte[] controlOutput = new byte[controlEncoder.PayloadSize];
        byte[] rleOutput = new byte[rleEncoder.PayloadSize];
        bool sawRle = false;

        for (int frame = 0; frame < 80; frame++)
        {
            byte[] pose = MakePose(quality, frame);
            int controlLength = controlEncoder.Encode(pose, (byte)frame, controlPacket, 0);
            int rleLength = rleEncoder.Encode(pose, (byte)frame, rlePacket, 0);
            Assert.True(controlLength > 0);
            Assert.True(rleLength > 0);

            Assert.True(controlDecoder.TryDecode(
                controlPacket, 0, controlLength, (byte)frame, controlOutput, out int controlConsumed));
            Assert.True(rleDecoder.TryDecode(
                rlePacket, 0, rleLength, (byte)frame, rleOutput, out int rleConsumed));
            Assert.Equal(controlLength, controlConsumed);
            Assert.Equal(rleLength, rleConsumed);
            Assert.Equal(controlOutput, rleOutput);
            AssertAuxiliaryEqual(pose, rleOutput, quality);
            sawRle |= rleEncoder.LastUsedZeroBoneRle;
        }

        Assert.True(sawRle, "separated-suffix RLE was never selected on the deterministic pose sequence");
    }

    [Fact]
    public void SeparatedSuffix_RleShrinksStationaryRotationStream()
    {
        const BitQuality quality = BitQuality.High;
        var control = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(
            quality, BasisNumerelSeparatedSuffixArmatureCodec.Options.Power2);
        var rle = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(
            quality, BasisNumerelSeparatedSuffixArmatureCodec.Options.Power2RleGray);
        byte[] controlPacket = new byte[control.MaxBodySize];
        byte[] rlePacket = new byte[rle.MaxBodySize];
        byte[] pose = MakeIdentityPayload(quality);

        // Seed both predictors and the exact auxiliary baseline.
        Assert.True(control.Encode(pose, 0, controlPacket, 0) > 0);
        Assert.True(rle.Encode(pose, 0, rlePacket, 0) > 0);

        bool selected = false;
        for (int frame = 1; frame < 32; frame++)
        {
            Assert.True(control.Encode(pose, (byte)frame, controlPacket, 0) > 0);
            Assert.True(rle.Encode(pose, (byte)frame, rlePacket, 0) > 0);
            if (!rle.LastUsedZeroBoneRle) continue;

            selected = true;
            Assert.True(rle.LastZeroBoneCount > 0);
            Assert.True(rle.LastZeroBoneRleNetSavedBits > 0);
            Assert.True(rle.LastRotationBytes < control.LastRotationBytes,
                $"RLE rotation body {rle.LastRotationBytes} B was not smaller than control {control.LastRotationBytes} B");
            Assert.Equal(control.LastAuxiliaryBytes, rle.LastAuxiliaryBytes);
            break;
        }
        Assert.True(selected, "zero-bone RLE was never selected for a stationary separated-suffix stream");
    }

    [Fact]
    public void SeparatedSuffix_LostUpdateDoesNotPoisonExactAuxiliarySuffix()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(quality);
        var decoder = new BasisNumerelSeparatedSuffixArmatureCodec.Decoder(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        byte[] pose0 = MakePose(quality, 0);
        int length = encoder.Encode(pose0, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out _));
        AssertAuxiliaryEqual(pose0, output, quality);

        // Sequence 1 is encoded but physically lost. Sequence 2 auxiliary fields are still
        // baseline-relative and therefore decode exactly without receiving sequence 1.
        Assert.True(encoder.Encode(MakePose(quality, 1), 1, packet, 0) > 0);
        byte[] pose2 = MakePose(quality, 2);
        length = encoder.Encode(pose2, 2, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 2, output, out int consumed));
        Assert.Equal(length, consumed);
        AssertAuxiliaryEqual(pose2, output, quality);
    }

    [Fact]
    public void SeparatedSuffix_TruncatedAuxiliaryIsRejectedBeforeNumerelSequenceCommit()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(quality);
        var decoder = new BasisNumerelSeparatedSuffixArmatureCodec.Decoder(quality);
        byte[] packet0 = new byte[encoder.MaxBodySize];
        byte[] packet1 = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        int len0 = encoder.Encode(MakePose(quality, 0), 0, packet0, 0);
        int len1 = encoder.Encode(MakePose(quality, 1), 1, packet1, 0);
        Assert.True(decoder.TryDecode(packet0, 0, len0, 0, output, out _));
        Assert.Equal((byte)0, decoder.LastSequence);

        Assert.False(decoder.TryDecode(packet1, 0, len1 - 1, 1, output, out _));
        Assert.Equal((byte)0, decoder.LastSequence);

        Assert.True(decoder.TryDecode(packet1, 0, len1, 1, output, out int consumed));
        Assert.Equal(len1, consumed);
        Assert.Equal((byte)1, decoder.LastSequence);
    }

    [Fact]
    public void SeparatedSuffix_DeadlinePredictionHoldsAuxiliaryAndRejectsLatePacket()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisNumerelSeparatedSuffixArmatureCodec.Encoder(quality);
        var decoder = new BasisNumerelSeparatedSuffixArmatureCodec.Decoder(quality);
        byte[] packet0 = new byte[encoder.MaxBodySize];
        byte[] packet1 = new byte[encoder.MaxBodySize];
        byte[] packet2 = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];
        byte[] pose0 = MakePose(quality, 0);
        byte[] pose1 = MakePose(quality, 1);
        byte[] pose2 = MakePose(quality, 2);

        int len0 = encoder.Encode(pose0, 0, packet0, 0);
        int len1 = encoder.Encode(pose1, 1, packet1, 0);
        int len2 = encoder.Encode(pose2, 2, packet2, 0);
        Assert.True(decoder.TryDecode(packet0, 0, len0, 0, output, out _));

        Assert.True(decoder.TryAdvanceDeadline(1, output));
        Assert.Equal((byte)1, decoder.LastSequence);
        AssertAuxiliaryEqual(pose0, output, quality);

        Assert.False(decoder.TryDecode(packet1, 0, len1, 1, output, out _));
        Assert.Equal((byte)1, decoder.LastSequence);

        Assert.True(decoder.TryDecode(packet2, 0, len2, 2, output, out _));
        Assert.Equal((byte)2, decoder.LastSequence);
        AssertAuxiliaryEqual(pose2, output, quality);
    }

    private static byte[] MakePose(BitQuality quality, int frame)
    {
        byte[] payload = MakeIdentityPayload(quality);
        float angle = frame * 0.025f;
        float half = angle * 0.5f;
        SetBoneQuaternion(payload, quality, 0, MathF.Sin(half), 0f, 0f, MathF.Cos(half));
        SetBoneQuaternion(payload, quality, 5, 0f, MathF.Sin(half * 0.7f), 0f, MathF.Cos(half * 0.7f));
        SetBoneQuaternion(payload, quality, 23, 0f, 0f, MathF.Sin(half * 0.4f), MathF.Cos(half * 0.4f));

        // Deterministic auxiliary movement, including the exact tail. End-effectors intentionally
        // stay at their bootstrap value so normal High packets do not pay 35 bytes unnecessarily.
        int pos = S.PosBytes(quality);
        for (int i = 0; i < pos; i++) payload[i] = (byte)(frame * 13 + i * 7);
        int tail = S.TailStart(quality);
        for (int i = 0; i < BasisBoneRotationCompression.TailBytes; i++)
            payload[tail + i] = (byte)(frame * 5 + i * 11);
        return payload;
    }

    private static byte[] MakeIdentityPayload(BitQuality quality)
    {
        byte[] payload = new byte[S.PayloadSize(quality)];
        for (int bone = 0; bone < S.BoneCount; bone++)
            SetBoneQuaternion(payload, quality, bone, 0f, 0f, 0f, 1f);
        return payload;
    }

    private static void SetBoneQuaternion(byte[] payload, BitQuality quality, int bone, float x, float y, float z, float w)
    {
        float length = MathF.Sqrt(x * x + y * y + z * z + w * w);
        x /= length; y /= length; z /= length; w /= length;
        byte bits = S.Bpc(quality)[bone];
        ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
            x, y, z, w, bits, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
        S.SetBone(payload, quality, bone, packed);
    }

    private static void AssertAuxiliaryEqual(byte[] expected, byte[] actual, BitQuality quality)
    {
        Assert.Equal(
            expected.AsSpan(0, S.PosBytes(quality)).ToArray(),
            actual.AsSpan(0, S.PosBytes(quality)).ToArray());
        Assert.Equal(
            expected.AsSpan(S.TailStart(quality), BasisBoneRotationCompression.TailBytes).ToArray(),
            actual.AsSpan(S.TailStart(quality), BasisBoneRotationCompression.TailBytes).ToArray());
        if (S.EndEffectorBytes(quality) > 0)
        {
            Assert.Equal(
                expected.AsSpan(S.EndEffectorOffset(quality), S.EndEffectorBytes(quality)).ToArray(),
                actual.AsSpan(S.EndEffectorOffset(quality), S.EndEffectorBytes(quality)).ToArray());
        }
    }
}
