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
    public void Fixed12BitStream_EncodesAllQualitiesAndRemainsNormalized()
    {
        foreach (BitQuality quality in S.AllQualities)
        {
            var options = BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous12Bit;
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
