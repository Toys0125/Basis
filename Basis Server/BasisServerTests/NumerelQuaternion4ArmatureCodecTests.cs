using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

public class NumerelQuaternion4ArmatureCodecTests
{
    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void IdentityStream_EncodesDecodesAndStaysNearIdentity(BitQuality quality)
    {
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        byte[] pose = MakeIdentityPayload(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        for (int frame = 0; frame < 32; frame++)
        {
            int length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(length > 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out int consumed));
            Assert.Equal(length, consumed);
            Assert.InRange(MaxAngularError(pose, output, quality), 0.0, 0.5);
        }
    }

    [Fact]
    public void SignContinuity_ReducesSmallestThreeBoundaryFlipCost()
    {
        const BitQuality quality = BitQuality.High;
        var raw = new BasisNumerelQuaternion4ArmatureCodec.Encoder(
            quality, BasisNumerelQuaternion4ArmatureCodec.Options.Upstream);
        var continuous = new BasisNumerelQuaternion4ArmatureCodec.Encoder(
            quality, BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous);
        byte[] packetA = new byte[raw.MaxBodySize];
        byte[] packetB = new byte[continuous.MaxBodySize];
        byte[] first = MakeIdentityPayload(quality);
        byte[] second = MakeIdentityPayload(quality);

        for (int bone = 0; bone < BasisBoneRotationCompression.SyncBoneCount; bone++)
        {
            SetBoneQuaternion(first, quality, bone, 0.714f, 0f, 0f, -0.700f);
            SetBoneQuaternion(second, quality, bone, 0.700f, 0f, 0f, -0.714f);
        }

        Assert.True(raw.Encode(first, 0, packetA, 0) > 0);
        Assert.True(continuous.Encode(first, 0, packetB, 0) > 0);
        Assert.True(raw.Encode(second, 1, packetA, 0) > 0);
        Assert.True(continuous.Encode(second, 1, packetB, 0) > 0);

        Assert.True(continuous.LastArmatureBits < raw.LastArmatureBits,
            $"continuous={continuous.LastArmatureBits}, raw={raw.LastArmatureBits}");
    }

    [Fact]
    public void Fixed12BitStreams_EncodeAllQualitiesAndRemainNormalized()
    {
        var tunings = new[]
        {
            BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous12Bit,
            BasisNumerelQuaternion4ArmatureCodec.Options.SquareRoot04Continuous12Bit,
            BasisNumerelQuaternion4ArmatureCodec.Options.NearestSquareRootContinuous12Bit,
        };

        foreach (var options in tunings)
        {
            foreach (BitQuality quality in S.AllQualities)
            {
                var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
                var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
                var rng = new Random(44120 + (int)quality);
                byte[] packet = new byte[encoder.MaxBodySize];
                byte[] output = new byte[encoder.PayloadSize];

                for (int frame = 0; frame < 16; frame++)
                {
                    byte[] pose = S.MakeRealisticPayload(quality, rng);
                    int length = encoder.Encode(pose, (byte)frame, packet, 0);
                    Assert.True(length > 0);
                    Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out int consumed));
                    Assert.Equal(length, consumed);
                    Assert.True(double.IsFinite(MaxAngularError(pose, output, quality)));
                }
            }
        }
    }

    [Fact]
    public void Power2RleGray_IdentityRun_RemovesZeroGolombBitsAndPreservesOutput()
    {
        const BitQuality quality = BitQuality.High;
        var controlEncoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(
            quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2Continuous16Bit);
        var rleEncoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(
            quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit);
        var controlDecoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(
            quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2Continuous16Bit);
        var rleDecoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(
            quality, BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit);
        byte[] pose = MakeIdentityPayload(quality);
        byte[] controlPacket = new byte[controlEncoder.MaxBodySize];
        byte[] rlePacket = new byte[rleEncoder.MaxBodySize];
        byte[] controlOutput = new byte[controlEncoder.PayloadSize];
        byte[] rleOutput = new byte[rleEncoder.PayloadSize];

        bool sawFullZeroRun = false;
        for (int frame = 0; frame < 64; frame++)
        {
            int controlLength = controlEncoder.Encode(pose, (byte)frame, controlPacket, 0);
            int rleLength = rleEncoder.Encode(pose, (byte)frame, rlePacket, 0);
            Assert.True(controlLength > 0 && rleLength > 0);
            Assert.True(controlDecoder.TryDecode(controlPacket, 0, controlLength, (byte)frame, controlOutput, out _));
            Assert.True(rleDecoder.TryDecode(rlePacket, 0, rleLength, (byte)frame, rleOutput, out _));
            Assert.Equal(controlOutput, rleOutput);

            if (rleEncoder.LastUsedZeroBoneRle
                && rleEncoder.LastZeroBoneCount == BasisBoneRotationCompression.SyncBoneCount)
            {
                sawFullZeroRun = true;
                Assert.Equal(1, rleEncoder.LastZeroRunCount);
                Assert.Equal(14, rleEncoder.LastZeroBoneRleMetadataBits);
                Assert.Equal(190, rleEncoder.LastZeroBoneRleNetSavedBits);
                Assert.Equal(controlEncoder.LastArmatureBits - 190, rleEncoder.LastArmatureBits);
                Assert.True(rleLength < controlLength);
            }
        }

        Assert.True(sawFullZeroRun, "stationary identity pose never converged to a full zero-bone RLE run");
    }

    [Fact]
    public void DeadlinePrediction_AdvancesImmediatelyAndNextPacketDoesNotDoubleApplyGap()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var deferred = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        var deadline = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        byte[] packet0 = new byte[encoder.MaxBodySize];
        byte[] packet1 = new byte[encoder.MaxBodySize];
        byte[] packet2 = new byte[encoder.MaxBodySize];
        byte[] packet3 = new byte[encoder.MaxBodySize];
        byte[] deferredOutput = new byte[encoder.PayloadSize];
        byte[] deadlineOutput = new byte[encoder.PayloadSize];

        byte[] pose0 = MakeIdentityPayload(quality);
        byte[] pose1 = MakeIdentityPayload(quality);
        byte[] pose2 = MakeIdentityPayload(quality);
        byte[] pose3 = MakeIdentityPayload(quality);
        SetBoneQuaternion(pose1, quality, 0, 0.0872f, 0f, 0f, 0.9962f);
        SetBoneQuaternion(pose2, quality, 0, 0.1736f, 0f, 0f, 0.9848f);
        SetBoneQuaternion(pose3, quality, 0, 0.2588f, 0f, 0f, 0.9659f);
        pose0[0] = 0x10;
        pose1[0] = 0x11;
        pose2[0] = 0x22;
        pose3[0] = 0x33;

        int len0 = encoder.Encode(pose0, 0, packet0, 0);
        int len1 = encoder.Encode(pose1, 1, packet1, 0);
        int len2 = encoder.Encode(pose2, 2, packet2, 0);
        int len3 = encoder.Encode(pose3, 3, packet3, 0);
        Assert.True(len0 > 0 && len1 > 0 && len2 > 0 && len3 > 0);

        foreach (var decoder in new[] { deferred, deadline })
        {
            byte[] output = ReferenceEquals(decoder, deferred) ? deferredOutput : deadlineOutput;
            Assert.True(decoder.TryDecode(packet0, 0, len0, 0, output, out _));
            Assert.True(decoder.TryDecode(packet1, 0, len1, 1, output, out _));
        }

        ulong beforePredictedBone = S.GetBone(deadlineOutput, quality, 0);
        Assert.True(deadline.TryAdvanceDeadline(2, deadlineOutput));
        Assert.Equal((byte)2, deadline.LastSequence);
        Assert.Equal(1, deadline.DeadlinePredictionsSinceDecode);
        Assert.NotEqual(beforePredictedBone, S.GetBone(deadlineOutput, quality, 0));
        Assert.Equal(pose1[0], deadlineOutput[0]); // missing-frame auxiliary data is held

        // A copy of sequence 2 arriving after its deadline cannot rewind the predictor.
        Assert.False(deadline.TryDecode(packet2, 0, len2, 2, deadlineOutput, out _));
        Assert.Equal((byte)2, deadline.LastSequence);

        // The deferred decoder discovers sequence 2 from the gap at sequence 3. Both paths must
        // reach the same state, proving the deadline prediction is not applied twice.
        Assert.True(deferred.TryDecode(packet3, 0, len3, 3, deferredOutput, out _));
        Assert.True(deadline.TryDecode(packet3, 0, len3, 3, deadlineOutput, out _));
        Assert.Equal(deferredOutput, deadlineOutput);
        Assert.Equal(0, deadline.DeadlinePredictionsSinceDecode);
    }

    [Fact]
    public void DeadlinePrediction_RequiresNextSequenceAndHonorsGapBudget()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        byte[] pose = MakeIdentityPayload(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];
        int length = encoder.Encode(pose, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out _));

        Assert.False(decoder.TryAdvanceDeadline(2, output));
        for (int sequence = 1; sequence <= 32; sequence++)
            Assert.True(decoder.TryAdvanceDeadline((byte)sequence, output));
        Assert.Equal(32, decoder.DeadlinePredictionsSinceDecode);
        Assert.False(decoder.TryAdvanceDeadline(33, output));

        // After 32 deadline predictions, one more unpredicted missing sequence exceeds the same
        // 32-frame safety budget used by ordinary deferred gap detection.
        for (int sequence = 1; sequence <= 34; sequence++)
            length = encoder.Encode(pose, (byte)sequence, packet, 0);
        Assert.False(decoder.TryDecode(packet, 0, length, 34, output, out _));
        Assert.Equal((byte)32, decoder.LastSequence);
    }

    [Fact]
    public void Power2RleGray_TruncatedRunHeader_IsRejectedTransactionally()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.Power2HybridRotation16Bit;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        byte[] pose = MakeIdentityPayload(quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];
        int length = 0;
        for (int frame = 0; frame < 32; frame++)
        {
            length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out _));
        }

        length = encoder.Encode(pose, 32, packet, 0);
        Assert.True(encoder.LastUsedZeroBoneRle);
        Assert.False(decoder.TryDecode(packet, 0, 1, 32, output, out _));
        Assert.Equal((byte)31, decoder.LastSequence);
        Assert.True(decoder.TryDecode(packet, 0, length, 32, output, out int consumed));
        Assert.Equal(length, consumed);
    }

    [Fact]
    public void PacketGap_AppliesNumerelPredictionAndFollowingPacketRemainsDecodable()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        var rng = new Random(44004);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        byte[] first = S.MakeRealisticPayload(quality, rng);
        int length = encoder.Encode(first, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out _));

        byte[] dropped = S.MakeRealisticPayload(quality, rng);
        Assert.True(encoder.Encode(dropped, 1, packet, 0) > 0);

        byte[] third = S.MakeRealisticPayload(quality, rng);
        length = encoder.Encode(third, 2, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 2, output, out int consumed));
        Assert.Equal(length, consumed);
        Assert.Equal((byte)2, decoder.LastSequence);
    }

    [Fact]
    public void LargeGap_IsRejectedTransactionallyUntilStreamReset()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuousAdaptive;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        var rng = new Random(44006);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        byte[] first = S.MakeRealisticPayload(quality, rng);
        int length = encoder.Encode(first, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, length, 0, output, out _));

        byte[] latest = first;
        for (int frame = 1; frame <= 40; frame++)
        {
            latest = S.MakeRealisticPayload(quality, rng);
            length = encoder.Encode(latest, (byte)frame, packet, 0);
            Assert.True(length > 0);
        }

        Assert.False(decoder.TryDecode(packet, 0, length - 1, 40, output, out _));
        Assert.Equal((byte)0, decoder.LastSequence);
        Assert.False(decoder.TryDecode(packet, 0, length, 40, output, out _));
        Assert.Equal((byte)0, decoder.LastSequence);

        decoder.Reset();
        Assert.True(decoder.TryDecode(packet, 0, length, 40, output, out int consumed));
        Assert.Equal(length, consumed);
        Assert.Equal((byte)40, decoder.LastSequence);
    }

    [Fact]
    public void TruncatedPacket_IsTransactional()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, options);
        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, options);
        var rng = new Random(44005);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        byte[] first = S.MakeRealisticPayload(quality, rng);
        int firstLength = encoder.Encode(first, 0, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, firstLength, 0, output, out _));

        byte[] second = S.MakeRealisticPayload(quality, rng);
        int secondLength = encoder.Encode(second, 1, packet, 0);
        Assert.True(secondLength > 1);
        Assert.False(decoder.TryDecode(packet, 0, secondLength - 1, 1, output, out _));
        Assert.Equal((byte)0, decoder.LastSequence);
        Assert.True(decoder.TryDecode(packet, 0, secondLength, 1, output, out int consumed));
        Assert.Equal(secondLength, consumed);
    }

    private static byte[] MakeIdentityPayload(BitQuality quality)
    {
        byte[] payload = new byte[S.PayloadSize(quality)];
        byte[] bpc = S.Bpc(quality);
        for (int bone = 0; bone < bpc.Length; bone++)
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

    private static double MaxAngularError(byte[] source, byte[] decoded, BitQuality quality)
    {
        double max = 0;
        byte[] bpc = S.Bpc(quality);
        for (int bone = 0; bone < bpc.Length; bone++)
        {
            ulong a = S.GetBone(source, quality, bone);
            ulong b = S.GetBone(decoded, quality, bone);
            BasisBoneRotationCompression.DecodeSmallestThree(a, bpc[bone], out float ax, out float ay, out float az, out float aw, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
            BasisBoneRotationCompression.DecodeSmallestThree(b, bpc[bone], out float bx, out float by, out float bz, out float bw, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
            double dot = Math.Abs(ax * bx + ay * by + az * bz + aw * bw);
            dot = Math.Clamp(dot, 0.0, 1.0);
            max = Math.Max(max, 2.0 * Math.Acos(dot) * 180.0 / Math.PI);
        }
        return max;
    }
}
