using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Basis.ImagePickup.Tests
{
    public class BasisJpegXlProfile1Tests
    {
        private static readonly byte[] Signature =
        {
            0x00, 0x00, 0x00, 0x0c, 0x4a, 0x58, 0x4c, 0x20,
            0x0d, 0x0a, 0x87, 0x0a,
        };

        private static readonly byte[] Ftyp =
        {
            0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
            0x6a, 0x78, 0x6c, 0x20, 0x00, 0x00, 0x00, 0x00,
            0x6a, 0x78, 0x6c, 0x20,
        };

        [Test]
        public void StageAAcceptsCanonicalOrderedJxlpContainer()
        {
            byte[] bytes = BuildContainer(
                (0U, new byte[] { 0xff, 0x0a }),
                (0x80000001U, new byte[] { 1, 2, 3 })
            );
            using var payload = new NativeArray<byte>(bytes, Allocator.Temp);

            Assert.That(
                BasisJpegXlProfile1.TryValidateStageA(
                    payload,
                    payload.Length,
                    BasisJpegXlProfile1.ProfileVersion,
                    out BasisProfile1StageAResult result,
                    out BasisProfile1RejectionCategory rejection,
                    out string error
                ),
                Is.True,
                error
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.None));
            Assert.That(result.PayloadBytes, Is.EqualTo(bytes.Length));
            Assert.That(result.JxlpBoxCount, Is.EqualTo(2));
            Assert.That(result.ConcatenatedCodestreamBytes, Is.EqualTo(5));
        }

        [Test]
        public void StageAAcceptsSingleFinalJxlp()
        {
            byte[] bytes = BuildContainer((0x80000000U, new byte[] { 0xff, 0x0a, 1 }));
            AssertStageA(bytes, true, BasisProfile1RejectionCategory.None);
        }

        [TestCase(0)]
        [TestCase(2)]
        public void StageARejectsWrongProfileVersion(int profileVersion)
        {
            byte[] bytes = BuildContainer((0x80000000U, new byte[] { 1 }));
            using var payload = new NativeArray<byte>(bytes, Allocator.Temp);

            bool ok = BasisJpegXlProfile1.TryValidateStageA(
                payload,
                payload.Length,
                (byte)profileVersion,
                out _,
                out BasisProfile1RejectionCategory rejection,
                out _
            );

            Assert.That(ok, Is.False);
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.UnsupportedProfile));
        }

        [Test]
        public void StageARejectsBadSignature()
        {
            byte[] bytes = BuildContainer((0x80000000U, new byte[] { 1 }));
            bytes[8] ^= 0x01;
            AssertStageA(bytes, false, BasisProfile1RejectionCategory.Malformed);
        }

        [Test]
        public void StageARejectsNoncanonicalFtyp()
        {
            byte[] bytes = BuildContainer((0x80000000U, new byte[] { 1 }));
            bytes[12 + 15] = 1;
            AssertStageA(bytes, false, BasisProfile1RejectionCategory.Malformed);
        }

        [Test]
        public void StageARejectsJxlcAndMetadataBoxes()
        {
            byte[] jxlc = BuildContainer((0x80000000U, new byte[] { 1 }));
            SetFirstPostFtypBoxType(jxlc, "jxlc");
            AssertStageA(jxlc, false, BasisProfile1RejectionCategory.Malformed);

            byte[] exif = BuildContainer((0x80000000U, new byte[] { 1 }));
            SetFirstPostFtypBoxType(exif, "Exif");
            AssertStageA(exif, false, BasisProfile1RejectionCategory.Malformed);
        }

        [Test]
        public void StageARejectsNonzeroSkippedAndDuplicateSequences()
        {
            AssertStageA(
                BuildContainer((0x80000001U, new byte[] { 1 })),
                false,
                BasisProfile1RejectionCategory.Malformed
            );
            AssertStageA(
                BuildContainer(
                    (0U, new byte[] { 1 }),
                    (0x80000002U, new byte[] { 2 })
                ),
                false,
                BasisProfile1RejectionCategory.Malformed
            );
            AssertStageA(
                BuildContainer(
                    (0U, new byte[] { 1 }),
                    (0x80000000U, new byte[] { 2 })
                ),
                false,
                BasisProfile1RejectionCategory.Malformed
            );
        }

        [Test]
        public void StageARejectsMissingFinalMarker()
        {
            AssertStageA(
                BuildContainer((0U, new byte[] { 1, 2 })),
                false,
                BasisProfile1RejectionCategory.Malformed
            );
        }

        [Test]
        public void StageARejectsDataOrBoxAfterFinalMarker()
        {
            byte[] trailingData = BuildContainer((0x80000000U, new byte[] { 1 }));
            Array.Resize(ref trailingData, trailingData.Length + 1);
            AssertStageA(trailingData, false, BasisProfile1RejectionCategory.Malformed);

            byte[] secondBox = BuildContainer(
                (0x80000000U, new byte[] { 1 }),
                (0x80000001U, new byte[] { 2 })
            );
            AssertStageA(secondBox, false, BasisProfile1RejectionCategory.Malformed);
        }

        [Test]
        public void StageARejectsTruncatedBoxSpan()
        {
            byte[] bytes = BuildContainer((0x80000000U, new byte[] { 1, 2, 3 }));
            Array.Resize(ref bytes, bytes.Length - 1);
            AssertStageA(bytes, false, BasisProfile1RejectionCategory.Malformed);
        }

        [Test]
        public void StageARejectsDeclaredLengthMismatch()
        {
            byte[] bytes = BuildContainer((0x80000000U, new byte[] { 1 }));
            using var payload = new NativeArray<byte>(bytes, Allocator.Temp);

            Assert.That(
                BasisJpegXlProfile1.TryValidateStageA(
                    payload,
                    payload.Length - 1,
                    BasisJpegXlProfile1.ProfileVersion,
                    out _,
                    out BasisProfile1RejectionCategory rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.Malformed));
        }

        [Test]
        public void StageARejectsNegativeDeclaredLengthAsMalformed()
        {
            using var payload = new NativeArray<byte>(1, Allocator.Temp);
            Assert.That(
                BasisJpegXlProfile1.TryValidateStageA(
                    payload,
                    -1,
                    BasisJpegXlProfile1.ProfileVersion,
                    out _,
                    out BasisProfile1RejectionCategory rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.Malformed));
        }

        [Test]
        public void StageARejectsPayloadLimitBeforeAllocatingLargeInput()
        {
            using var payload = new NativeArray<byte>(1, Allocator.Temp);
            Assert.That(
                BasisJpegXlProfile1.TryValidateStageA(
                    payload,
                    BasisJpegXlProfile1.MaximumPayloadBytes + 1,
                    BasisJpegXlProfile1.ProfileVersion,
                    out _,
                    out BasisProfile1RejectionCategory rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.PayloadLimitExceeded));
        }

        [Test]
        public void SubmittedCanvasPixelArithmeticCoversBoundaryAndOverflow()
        {
            Assert.That(
                BasisJpegXlProfile1.TryCalculateSubmittedCanvasPixels(
                    2048,
                    2048,
                    8,
                    out ulong atLimit
                ),
                Is.True
            );
            Assert.That(atLimit, Is.EqualTo(BasisJpegXlProfile1.MaximumSubmittedCanvasPixels));

            Assert.That(
                BasisJpegXlProfile1.TryCalculateSubmittedCanvasPixels(
                    2048,
                    2048,
                    9,
                    out ulong overLimit
                ),
                Is.True
            );
            Assert.That(overLimit, Is.EqualTo(37_748_736UL));

            Assert.That(
                BasisJpegXlProfile1.TryCalculateSubmittedCanvasPixels(
                    ulong.MaxValue,
                    2,
                    1,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void BaseTimelineArithmeticCoversBoundaryAndOverflow()
        {
            Assert.That(
                BasisJpegXlProfile1.TryCalculateBaseTimelineDurationMicroseconds(
                    new long[] { 150_000_000, 150_000_000 },
                    out ulong atLimit
                ),
                Is.True
            );
            Assert.That(atLimit, Is.EqualTo(300_000_000UL));

            Assert.That(
                BasisJpegXlProfile1.TryCalculateBaseTimelineDurationMicroseconds(
                    new long[] { long.MaxValue, long.MaxValue, long.MaxValue },
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void StageBEnvelopeUsesExactTimebaseAndOnePlaythroughTimeline()
        {
            long[] durations = { 150_000_000, 150_000_000 };
            Assert.That(
                BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    1,
                    1,
                    2,
                    durations,
                    BasisJpegXlProfile1.TimebaseNumerator,
                    BasisJpegXlProfile1.TimebaseDenominator,
                    out ulong submittedPixels,
                    out ulong timeline,
                    out BasisProfile1RejectionCategory rejection,
                    out string error
                ),
                Is.True,
                error
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.None));
            Assert.That(submittedPixels, Is.EqualTo(2));
            Assert.That(timeline, Is.EqualTo(300_000_000UL));

            Assert.That(
                BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    1,
                    1,
                    2,
                    durations,
                    2_000_000,
                    2,
                    out _,
                    out _,
                    out rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.UnsupportedProfile));
        }

        [Test]
        public void StageBEnvelopeRejectsSubmittedDurationAndTimelineLimits()
        {
            long[] nineDurations = new long[9];
            for (int i = 0; i < nineDurations.Length; i++)
                nineDurations[i] = BasisJpegXlProfile1.MinimumFrameDurationMicroseconds;
            Assert.That(
                BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    2048,
                    2048,
                    9,
                    nineDurations,
                    BasisJpegXlProfile1.TimebaseNumerator,
                    BasisJpegXlProfile1.TimebaseDenominator,
                    out _,
                    out _,
                    out BasisProfile1RejectionCategory rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.SharedLimitExceeded));

            Assert.That(
                BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    1,
                    1,
                    1,
                    new long[] { BasisJpegXlProfile1.MinimumFrameDurationMicroseconds - 1 },
                    BasisJpegXlProfile1.TimebaseNumerator,
                    BasisJpegXlProfile1.TimebaseDenominator,
                    out _,
                    out _,
                    out rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.SharedLimitExceeded));

            Assert.That(
                BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    1,
                    1,
                    1,
                    new long[] { 300_000_001 },
                    BasisJpegXlProfile1.TimebaseNumerator,
                    BasisJpegXlProfile1.TimebaseDenominator,
                    out _,
                    out _,
                    out rejection,
                    out _
                ),
                Is.False
            );
            Assert.That(rejection, Is.EqualTo(BasisProfile1RejectionCategory.SharedLimitExceeded));
        }

        [Test]
        public void CanonicalPatchConversionHasExactlyOnePointZeroInflationAndPreservesHiddenRgb()
        {
            var hidden = new Color32(17, 39, 201, 0);
            var first = new[] { hidden, new Color32(1, 2, 3, 255) };
            var second = new[] { new Color32(4, 5, 6, 128), new Color32(7, 8, 9, 255) };
            long[] durations = { 33_334, 50_001 };

            Assert.That(
                BasisProfile1PatchConverter.TryCreate(
                    2,
                    1,
                    0,
                    durations,
                    new[] { first, second },
                    out BasisAnimatedImageData data,
                    out string error
                ),
                Is.True,
                error
            );

            using (data)
            {
                Assert.That(data.DecodedFramePixels, Is.EqualTo(4));
                Assert.That(data.TotalDurationMicroseconds, Is.EqualTo(83_335));
                Assert.That(data.TotalPlayCount, Is.EqualTo(0));
                for (int i = 0; i < data.FrameCount; i++)
                {
                    BasisAnimatedImageFrame frame = data.GetFrame(i);
                    Assert.That(frame.X, Is.EqualTo(0));
                    Assert.That(frame.Y, Is.EqualTo(0));
                    Assert.That(frame.Width, Is.EqualTo(2));
                    Assert.That(frame.Height, Is.EqualTo(1));
                    Assert.That(frame.Blend, Is.EqualTo(BasisAnimationBlend.Source));
                    Assert.That(frame.Disposal, Is.EqualTo(BasisAnimationDisposal.None));
                }
                Assert.That(data.CopyFramePixelsToManaged(0)[0], Is.EqualTo(hidden));
            }
        }

        [Test]
        public void Profile1PreservesFullUint32LoopCountWhileV2FailsClosed()
        {
            Assert.That(
                BasisProfile1PatchConverter.TryCreate(
                    1,
                    1,
                    uint.MaxValue,
                    new long[] { BasisJpegXlProfile1.MinimumFrameDurationMicroseconds },
                    new[] { new[] { new Color32(1, 2, 3, 4) } },
                    out BasisAnimatedImageData data,
                    out string error
                ),
                Is.True,
                error
            );

            using (data)
            {
                Assert.That(data.TotalPlayCount, Is.EqualTo((long)uint.MaxValue));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => new BasisBurstAnimationEncodeRequest(data)
                );
            }
        }

        [Test]
        public void UnknownDesktopMemoryFallsBackToLowMemoryReceiverClass()
        {
            const long lowDecodedPixels = 16L * 1024L * 1024L;
            Assert.That(
                BasisImagePickupSettings.CalculateAnimationMemoryLimits(0, false).DecodedFramePixelsPerSender,
                Is.EqualTo(lowDecodedPixels)
            );
            Assert.That(
                BasisImagePickupSettings.CalculateAnimationMemoryLimits(-1, false).DecodedFramePixelsPerSender,
                Is.EqualTo(lowDecodedPixels)
            );
            Assert.That(
                BasisImagePickupSettings.CalculateAnimationMemoryLimits(4096, false).DecodedFramePixelsPerSender,
                Is.EqualTo(lowDecodedPixels)
            );
        }

        [Test]
        public void SenderAdmissionUsesExplicitDesktopClassesAndMobileV2OnlyPolicy()
        {
            BasisProfile1SenderPolicy unknown = BasisJpegXlProfile1SenderAdmission.GetPolicy(0, false);
            Assert.That(unknown.MemoryClass, Is.EqualTo(BasisProfile1SenderMemoryClass.Low));
            Assert.That(unknown.MaximumSubmittedCanvasPixels, Is.EqualTo(8UL * 1024UL * 1024UL));
            Assert.That(unknown.RecommendedThreads, Is.EqualTo(2));

            BasisProfile1SenderPolicy middle = BasisJpegXlProfile1SenderAdmission.GetPolicy(8192, false);
            Assert.That(middle.MemoryClass, Is.EqualTo(BasisProfile1SenderMemoryClass.Middle));
            Assert.That(
                middle.MaximumSubmittedCanvasPixels,
                Is.EqualTo(BasisJpegXlProfile1.MaximumSubmittedCanvasPixels)
            );

            BasisProfile1SenderPolicy high = BasisJpegXlProfile1SenderAdmission.GetPolicy(8193, false);
            Assert.That(high.MemoryClass, Is.EqualTo(BasisProfile1SenderMemoryClass.High));

            BasisProfile1SenderPolicy mobile = BasisJpegXlProfile1SenderAdmission.GetPolicy(16384, true);
            Assert.That(mobile.CanEncodeProfile1, Is.False);
        }

        [Test]
        public void SenderAdmissionUsesMeasuredWorkingSetAndWireCeiling()
        {
            Assert.That(
                BasisJpegXlProfile1SenderAdmission.TryAdmit(
                    0,
                    false,
                    8UL * 1024UL * 1024UL,
                    2,
                    out BasisProfile1SenderPolicy low,
                    out long lowEstimate,
                    out string lowReason
                ),
                Is.True,
                lowReason
            );
            Assert.That(lowEstimate, Is.LessThanOrEqualTo(low.DedicatedEncoderBudgetBytes));

            Assert.That(
                BasisJpegXlProfile1SenderAdmission.TryAdmit(
                    0,
                    false,
                    8UL * 1024UL * 1024UL + 1,
                    2,
                    out _,
                    out _,
                    out _
                ),
                Is.False
            );

            Assert.That(
                BasisJpegXlProfile1SenderAdmission.TryAdmit(
                    8192,
                    false,
                    BasisJpegXlProfile1.MaximumSubmittedCanvasPixels,
                    4,
                    out BasisProfile1SenderPolicy middle,
                    out long middleEstimate,
                    out string middleReason
                ),
                Is.True,
                middleReason
            );
            Assert.That(middleEstimate, Is.LessThanOrEqualTo(middle.DedicatedEncoderBudgetBytes));

            Assert.That(
                BasisJpegXlProfile1SenderAdmission.TryAdmit(
                    16384,
                    true,
                    1,
                    1,
                    out _,
                    out _,
                    out _
                ),
                Is.False
            );
        }

        [Test]
        public void PlaybackWatermarkClampsBackwardCorrectionAndResetsForNewEpoch()
        {
            long epoch = 1_000_000;
            long watermarkEpoch = 0;
            long watermarkMicroseconds = 0;

            Assert.That(
                BasisAnimatedImagePlayer.ResolveMonotonicPlaybackTargetMicroseconds(
                    epoch + 109,
                    epoch,
                    ref watermarkEpoch,
                    ref watermarkMicroseconds
                ),
                Is.EqualTo(10)
            );
            Assert.That(
                BasisAnimatedImagePlayer.ResolveMonotonicPlaybackTargetMicroseconds(
                    epoch + 50,
                    epoch,
                    ref watermarkEpoch,
                    ref watermarkMicroseconds
                ),
                Is.EqualTo(10)
            );
            Assert.That(
                BasisAnimatedImagePlayer.ResolveMonotonicPlaybackTargetMicroseconds(
                    epoch + 209,
                    epoch,
                    ref watermarkEpoch,
                    ref watermarkMicroseconds
                ),
                Is.EqualTo(20)
            );

            long newEpoch = 2_000_000;
            Assert.That(
                BasisAnimatedImagePlayer.ResolveMonotonicPlaybackTargetMicroseconds(
                    newEpoch + 39,
                    newEpoch,
                    ref watermarkEpoch,
                    ref watermarkMicroseconds
                ),
                Is.EqualTo(3)
            );
            Assert.That(watermarkEpoch, Is.EqualTo(newEpoch));
            Assert.That(watermarkMicroseconds, Is.EqualTo(3));
        }

        private static void AssertStageA(
            byte[] bytes,
            bool expectedOk,
            BasisProfile1RejectionCategory expectedRejection
        )
        {
            using var payload = new NativeArray<byte>(bytes, Allocator.Temp);
            bool ok = BasisJpegXlProfile1.TryValidateStageA(
                payload,
                payload.Length,
                BasisJpegXlProfile1.ProfileVersion,
                out _,
                out BasisProfile1RejectionCategory rejection,
                out _
            );
            Assert.That(ok, Is.EqualTo(expectedOk));
            Assert.That(rejection, Is.EqualTo(expectedRejection));
        }

        private static byte[] BuildContainer(params (uint Counter, byte[] Codestream)[] boxes)
        {
            var bytes = new List<byte>(Signature.Length + Ftyp.Length + boxes.Length * 16);
            bytes.AddRange(Signature);
            bytes.AddRange(Ftyp);
            foreach ((uint counter, byte[] codestream) in boxes)
            {
                byte[] data = codestream ?? Array.Empty<byte>();
                WriteUInt32BigEndian(bytes, checked((uint)(12 + data.Length)));
                bytes.Add((byte)'j');
                bytes.Add((byte)'x');
                bytes.Add((byte)'l');
                bytes.Add((byte)'p');
                WriteUInt32BigEndian(bytes, counter);
                bytes.AddRange(data);
            }
            return bytes.ToArray();
        }

        private static void SetFirstPostFtypBoxType(byte[] bytes, string boxType)
        {
            int offset = Signature.Length + Ftyp.Length + 4;
            for (int i = 0; i < 4; i++)
                bytes[offset + i] = (byte)boxType[i];
        }

        private static void WriteUInt32BigEndian(List<byte> destination, uint value)
        {
            destination.Add((byte)(value >> 24));
            destination.Add((byte)(value >> 16));
            destination.Add((byte)(value >> 8));
            destination.Add((byte)value);
        }
    }
}
