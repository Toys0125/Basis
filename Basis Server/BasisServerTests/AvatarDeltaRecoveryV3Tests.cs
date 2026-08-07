using Basis.Network.Core.Compression;
using Xunit;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using S = BasisServerTests.DeltaTestSupport;

namespace BasisServerTests;

public class AvatarDeltaRecoveryV3Tests
{
    [Fact]
    public void DefaultSchedule_RefreshesEveryGroupOncePerCompleteCycle()
    {
        var options = BasisAvatarDeltaRecoveryV3.Options.Default;
        int cycle = options.RefreshCycleFrames;

        for (int start = 0; start + cycle <= 250; start += cycle)
        {
            int refreshFrames = 0;
            byte groups = 0;
            for (int i = 0; i < cycle; i++)
            {
                byte mask = BasisAvatarDeltaRecoveryV3.ScheduledRefreshMask((byte)(start + i), options);
                if (mask != 0) refreshFrames++;
                groups |= mask;
            }

            Assert.Equal(BasisAvatarDeltaRecoveryV3.GroupCount, refreshFrames);
            Assert.Equal((byte)0xFF, groups);
        }
    }

    [Fact]
    public void DefaultSchedule_BoundsRefreshGapAcrossSequenceWrap()
    {
        var options = BasisAvatarDeltaRecoveryV3.Options.Default;
        for (int group = 0; group < BasisAvatarDeltaRecoveryV3.GroupCount; group++)
        {
            var positions = new List<int>();
            for (int sequence = 0; sequence < 256; sequence++)
            {
                byte mask = BasisAvatarDeltaRecoveryV3.ScheduledRefreshMask((byte)sequence, options);
                if ((mask & (1 << group)) != 0) positions.Add(sequence);
            }

            Assert.NotEmpty(positions);
            int maxGap = 0;
            for (int i = 0; i < positions.Count; i++)
            {
                int next = positions[(i + 1) % positions.Count];
                int gap = (next - positions[i] + 256) & 255;
                maxGap = Math.Max(maxGap, gap);
            }
            Assert.InRange(maxGap, 1, 9);
        }
    }

    [Theory]
    [InlineData(BitQuality.VeryLow)]
    [InlineData(BitQuality.Low)]
    [InlineData(BitQuality.Medium)]
    [InlineData(BitQuality.High)]
    public void NoLoss_AfterInitialShardCycle_EveryFrameIsExact(BitQuality quality)
    {
        var options = BasisAvatarDeltaRecoveryV3.Options.Default;
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(quality, options);
        var decoder = new BasisAvatarDeltaRecoveryV3.Decoder(quality, options);
        var rng = new Random(30203 + (int)quality);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];
        bool sawFullSync = false;

        for (int frame = 0; frame < 64; frame++)
        {
            byte[] current = S.MakeRealisticPayload(quality, rng);
            int length = encoder.Encode(current, (byte)frame, packet, 0);
            Assert.True(length > 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out int consumed));
            Assert.Equal(length, consumed);

            if (decoder.IsFullySynchronized)
            {
                sawFullSync = true;
                Assert.Equal(current, output);
            }
        }

        Assert.True(sawFullSync);
    }

    [Fact]
    public void LostRefresh_InvalidatesOnlyThatGroup_ThenRecoversExactly()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisAvatarDeltaRecoveryV3.Options.Default;
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(quality, options);
        var decoder = new BasisAvatarDeltaRecoveryV3.Decoder(quality, options);
        var rng = new Random(8817);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];

        int frame = 0;
        for (; frame < 20; frame++)
        {
            byte[] pose = S.MakeRealisticPayload(quality, rng);
            int length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out _));
        }
        Assert.True(decoder.IsFullySynchronized);

        int lostFrame = -1;
        byte lostGroup = 0;
        for (int candidate = frame; candidate < frame + 16; candidate++)
        {
            byte mask = BasisAvatarDeltaRecoveryV3.ScheduledRefreshMask((byte)candidate, options);
            byte next = BasisAvatarDeltaRecoveryV3.ScheduledRefreshMask((byte)(candidate + 1), options);
            if (mask != 0 && mask != next)
            {
                lostFrame = candidate;
                lostGroup = mask;
                break;
            }
        }
        Assert.True(lostFrame >= 0);

        while (frame < lostFrame)
        {
            byte[] pose = S.MakeRealisticPayload(quality, rng);
            int length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out _));
            frame++;
        }

        byte[] dropped = S.MakeRealisticPayload(quality, rng);
        Assert.True(encoder.Encode(dropped, (byte)frame, packet, 0) > 0);
        frame++;

        byte[] currentAfterGap = S.MakeRealisticPayload(quality, rng);
        int recoveryLength = encoder.Encode(currentAfterGap, (byte)frame, packet, 0);
        Assert.True(decoder.TryDecode(packet, 0, recoveryLength, (byte)frame, output, out _));
        Assert.Equal((byte)(0xFF & ~lostGroup), decoder.ValidGroupMask);

        int lostGroupIndex = SingleGroupIndex(lostGroup);
        for (int bone = 0; bone < BasisBoneRotationCompression.SyncBoneCount; bone++)
        {
            if (BasisAvatarDeltaRecoveryV3.GetBoneGroup(bone) == lostGroupIndex) continue;
            Assert.Equal(S.GetBone(currentAfterGap, quality, bone), S.GetBone(output, quality, bone));
        }

        bool recovered = false;
        for (frame++; frame < lostFrame + 24; frame++)
        {
            byte[] pose = S.MakeRealisticPayload(quality, rng);
            int length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out _));
            if ((decoder.ValidGroupMask & lostGroup) != 0)
            {
                Assert.True(decoder.IsFullySynchronized);
                Assert.Equal(pose, output);
                recovered = true;
                break;
            }
        }
        Assert.True(recovered);
    }

    [Fact]
    public void PeriodicLoss_DoesNotCreatePermanentBaselineStall()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisAvatarDeltaRecoveryV3.Options.Default;
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(quality, options);
        var decoder = new BasisAvatarDeltaRecoveryV3.Decoder(quality, options);
        var rng = new Random(91010);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] output = new byte[encoder.PayloadSize];
        byte groupsSeenValid = 0;
        int accepted = 0;
        int fullySynchronizedFrames = 0;

        for (int frame = 0; frame < 160; frame++)
        {
            byte[] pose = S.MakeRealisticPayload(quality, rng);
            int length = encoder.Encode(pose, (byte)frame, packet, 0);
            Assert.True(length > 0);

            // Deliberately drop every tenth packet, the phase that permanently destroys the
            // monolithic 500 ms keyframe baseline in the benchmark's adversarial loss case.
            if (frame % 10 == 0) continue;

            Assert.True(decoder.TryDecode(packet, 0, length, (byte)frame, output, out _));
            accepted++;
            groupsSeenValid |= decoder.ValidGroupMask;
            if (decoder.IsFullySynchronized)
            {
                fullySynchronizedFrames++;
                Assert.Equal(pose, output);
            }
        }

        Assert.Equal(144, accepted);
        Assert.Equal((byte)0xFF, groupsSeenValid);
        Assert.True(fullySynchronizedFrames > 0);
    }

    [Fact]
    public void RefreshShardMissingRequiredField_IsRejected()
    {
        const BitQuality quality = BitQuality.High;
        var options = BasisAvatarDeltaRecoveryV3.Options.Default;
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(quality, options);
        var decoder = new BasisAvatarDeltaRecoveryV3.Decoder(quality, options);
        byte[] pose = S.MakeRealisticPayload(quality, new Random(7701));
        byte[] packet = new byte[encoder.MaxBodySize];

        byte sequence = 0;
        byte refresh = BasisAvatarDeltaRecoveryV3.ScheduledRefreshMask(sequence, options);
        Assert.NotEqual((byte)0, refresh);
        int group = SingleGroupIndex(refresh);
        int field = Enumerable.Range(0, BasisAvatarDeltaCompression.FieldCount)
            .First(f => BasisAvatarDeltaRecoveryV3.GetFieldGroup(f) == group);

        int length = encoder.Encode(pose, sequence, packet, 0);
        Assert.True(length > BasisAvatarDeltaCompression.DirtyMaskBytes);
        packet[field >> 3] &= (byte)~(1 << (field & 7));

        byte[] output = new byte[encoder.PayloadSize];
        Assert.False(decoder.TryDecode(packet, 0, length, sequence, output, out _));
        Assert.False(decoder.HasSequence);
        Assert.Equal((byte)0, decoder.ValidGroupMask);
    }

    [Fact]
    public void TruncatedAndStalePackets_DoNotCommitDecoderState()
    {
        const BitQuality quality = BitQuality.High;
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(quality);
        var underTest = new BasisAvatarDeltaRecoveryV3.Decoder(quality);
        var control = new BasisAvatarDeltaRecoveryV3.Decoder(quality);
        var rng = new Random(4422);
        byte[] packet = new byte[encoder.MaxBodySize];
        byte[] a = new byte[encoder.PayloadSize];
        byte[] b = new byte[encoder.PayloadSize];

        byte[] first = S.MakeRealisticPayload(quality, rng);
        int firstLength = encoder.Encode(first, 0, packet, 0);
        Assert.True(underTest.TryDecode(packet, 0, firstLength, 0, a, out _));
        Assert.True(control.TryDecode(packet, 0, firstLength, 0, b, out _));
        Assert.Equal(b, a);

        byte validBefore = underTest.ValidGroupMask;
        byte[] second = S.MakeRealisticPayload(quality, rng);
        int secondLength = encoder.Encode(second, 1, packet, 0);
        Assert.False(underTest.TryDecode(packet, 0, secondLength - 1, 1, a, out _));
        Assert.Equal((byte)0, underTest.LastSequence);
        Assert.Equal(validBefore, underTest.ValidGroupMask);

        Assert.True(underTest.TryDecode(packet, 0, secondLength, 1, a, out _));
        Assert.True(control.TryDecode(packet, 0, secondLength, 1, b, out _));
        Assert.Equal(b, a);
        Assert.Equal(control.ValidGroupMask, underTest.ValidGroupMask);

        Assert.False(underTest.TryDecode(packet, 0, secondLength, 1, a, out _));
        Assert.Equal((byte)1, underTest.LastSequence);
    }

    private static int SingleGroupIndex(byte mask)
    {
        for (int i = 0; i < BasisAvatarDeltaRecoveryV3.GroupCount; i++)
            if (mask == (1 << i)) return i;
        throw new ArgumentOutOfRangeException(nameof(mask));
    }
}
