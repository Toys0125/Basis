using System;
using System.Threading;
using Basis.ImageSandbox;
using Unity.Collections;
using UnityEngine;

namespace Basis.ImagePickup
{
    /// <summary>
    /// Host boundary for Profile 1 Stage B. This type consumes only bounded values emitted by the
    /// pinned sandbox module; it does not inspect JPEG XL codestream structure itself.
    /// </summary>
    internal static class BasisJpegXlProfile1Sandbox
    {
        public static bool TryPreflight(
            BasisProfile1SandboxDecoder decoder,
            byte[] canonicalContainer,
            CancellationToken cancellationToken,
            out BasisProfile1StageBResourceEnvelope envelope,
            out BasisProfile1SandboxPreflight sandboxPreflight,
            out long[] frameDurationsMicroseconds,
            out BasisProfile1RejectionCategory rejection,
            out string error
        )
        {
            envelope = default;
            sandboxPreflight = default;
            frameDurationsMicroseconds = null;
            rejection = BasisProfile1RejectionCategory.None;
            error = null;

            if (decoder == null)
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 sandbox decoder is unavailable.";
                return false;
            }

            BasisProfile1SandboxPreflight result = decoder.Preflight(
                canonicalContainer,
                cancellationToken
            );
            if (result.Status != BasisProfile1SandboxStatus.Success)
            {
                rejection = MapStatus(result.Status);
                error = $"Profile 1 Stage B preflight failed: {result.Status}.";
                return false;
            }

            if (
                result.Width > int.MaxValue
                || result.Height > int.MaxValue
                || result.LogicalFrameCount > int.MaxValue
                || result.FrameDurationsMicroseconds == null
                || result.FrameDurationsMicroseconds.Length != result.LogicalFrameCount
            )
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 sandbox returned an invalid bounded resource envelope.";
                return false;
            }

            int frameCount = (int)result.LogicalFrameCount;
            frameDurationsMicroseconds = new long[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                ulong duration = result.FrameDurationsMicroseconds[i];
                if (duration > long.MaxValue)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 sandbox returned an invalid frame duration.";
                    frameDurationsMicroseconds = null;
                    return false;
                }
                frameDurationsMicroseconds[i] = (long)duration;
            }

            if (
                !BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    (int)result.Width,
                    (int)result.Height,
                    frameCount,
                    frameDurationsMicroseconds,
                    BasisJpegXlProfile1.TimebaseNumerator,
                    BasisJpegXlProfile1.TimebaseDenominator,
                    out ulong submittedCanvasPixels,
                    out ulong baseTimelineMicroseconds,
                    out rejection,
                    out error
                )
            )
            {
                frameDurationsMicroseconds = null;
                return false;
            }

            if (
                submittedCanvasPixels != result.SubmittedCanvasPixels
                || baseTimelineMicroseconds != result.BaseTimelineMicroseconds
            )
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 sandbox and host accounting disagreed.";
                frameDurationsMicroseconds = null;
                return false;
            }

            sandboxPreflight = result;
            envelope = new BasisProfile1StageBResourceEnvelope(
                (int)result.Width,
                (int)result.Height,
                frameCount,
                result.TotalPlayCount,
                submittedCanvasPixels,
                baseTimelineMicroseconds,
                result.PublicRegularLayerCount,
                result.PublicRegularLayerPixels,
                result.CroppedLayerCount,
                result.ReferenceReadEdges,
                result.SavedReferenceCount,
                result.BlendOperationCount,
                result.MaximumReferenceChainDepth,
                result.PreviewPixels
            );
            return true;
        }

        /// <summary>
        /// Performs the full pixel pass after the caller has completed host residency/aggregate
        /// admission. Admission must include the native frame pool allocated here, one reusable
        /// RGBA8 canvas, the sandbox linear-memory cap, and any concurrently live payload storage.
        /// The complete logical timeline is never duplicated as managed frame arrays.
        /// </summary>
        public static bool TryDecodeAdmitted(
            BasisProfile1SandboxDecoder decoder,
            byte[] canonicalContainer,
            BasisProfile1StageBResourceEnvelope envelope,
            BasisProfile1SandboxPreflight sandboxPreflight,
            long[] frameDurationsMicroseconds,
            CancellationToken cancellationToken,
            out BasisAnimatedImageData data,
            out BasisProfile1RejectionCategory rejection,
            out string error
        )
        {
            data = null;
            rejection = BasisProfile1RejectionCategory.None;
            error = null;

            if (
                decoder == null
                || canonicalContainer == null
                || canonicalContainer.Length == 0
                || envelope.CanvasWidth <= 0
                || envelope.CanvasHeight <= 0
                || envelope.LogicalFrameCount <= 0
                || frameDurationsMicroseconds == null
                || frameDurationsMicroseconds.Length != envelope.LogicalFrameCount
                || envelope.SubmittedCanvasPixels > int.MaxValue
                || sandboxPreflight.Status != BasisProfile1SandboxStatus.Success
                || sandboxPreflight.Width != (uint)envelope.CanvasWidth
                || sandboxPreflight.Height != (uint)envelope.CanvasHeight
                || sandboxPreflight.LogicalFrameCount != (uint)envelope.LogicalFrameCount
                || sandboxPreflight.TotalPlayCount != envelope.TotalPlayCount
                || sandboxPreflight.SubmittedCanvasPixels != envelope.SubmittedCanvasPixels
                || sandboxPreflight.BaseTimelineMicroseconds
                    != envelope.BaseTimelineDurationMicroseconds
            )
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 admitted decode received an invalid resource envelope.";
                return false;
            }

            int canvasPixels;
            int totalPixels;
            int canvasBytes;
            try
            {
                canvasPixels = checked(envelope.CanvasWidth * envelope.CanvasHeight);
                totalPixels = checked(canvasPixels * envelope.LogicalFrameCount);
                canvasBytes = checked(canvasPixels * 4);
            }
            catch (OverflowException)
            {
                rejection = BasisProfile1RejectionCategory.MemoryAdmissionDenied;
                error = "Profile 1 decoded allocation arithmetic overflowed.";
                return false;
            }
            if ((ulong)totalPixels != envelope.SubmittedCanvasPixels)
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 admitted decode pixel count disagrees with Stage B preflight.";
                return false;
            }

            NativeArray<BasisAnimatedImageFrame> frames = default;
            NativeArray<Color32> pixels = default;
            NativeArray<long> frameEnds = default;
            bool adopted = false;
            try
            {
                frames = new NativeArray<BasisAnimatedImageFrame>(
                    envelope.LogicalFrameCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                pixels = new NativeArray<Color32>(
                    totalPixels,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );
                frameEnds = new NativeArray<long>(
                    envelope.LogicalFrameCount,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory
                );

                long endTime = 0;
                for (int frameIndex = 0; frameIndex < envelope.LogicalFrameCount; frameIndex++)
                {
                    long duration = frameDurationsMicroseconds[frameIndex];
                    endTime = checked(endTime + duration);
                    frames[frameIndex] = new BasisAnimatedImageFrame
                    {
                        X = 0,
                        Y = 0,
                        Width = envelope.CanvasWidth,
                        Height = envelope.CanvasHeight,
                        PixelOffset = checked(frameIndex * canvasPixels),
                        PixelCount = canvasPixels,
                        DurationMicroseconds = duration,
                        EndTimeMicroseconds = endTime,
                        Blend = BasisAnimationBlend.Source,
                        Disposal = BasisAnimationDisposal.None,
                        Reserved = 0,
                    };
                    frameEnds[frameIndex] = endTime;
                }
                if ((ulong)endTime != envelope.BaseTimelineDurationMicroseconds)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 admitted decode timeline disagrees with Stage B preflight.";
                    return false;
                }

                bool hasAnyAlpha = true; // Transparent canonical background.
                bool hasPartialAlpha = false;
                bool callbackMalformed = false;
                int expectedFrameIndex = 0;
                BasisProfile1SandboxStatus decodeStatus = decoder.DecodeFrames(
                    canonicalContainer,
                    sandboxPreflight,
                    (frameIndex, rgba, duration) =>
                    {
                        if (
                            frameIndex != expectedFrameIndex
                            || frameIndex >= frameDurationsMicroseconds.Length
                            || duration != (ulong)frameDurationsMicroseconds[frameIndex]
                            || rgba == null
                            || rgba.Length != canvasBytes
                        )
                        {
                            callbackMalformed = true;
                            return false;
                        }

                        int destinationOffset = checked(frameIndex * canvasPixels);
                        int sourceOffset = 0;
                        for (int pixelIndex = 0; pixelIndex < canvasPixels; pixelIndex++)
                        {
                            byte alpha = rgba[sourceOffset + 3];
                            if (alpha != byte.MaxValue)
                                hasAnyAlpha = true;
                            if (alpha > 0 && alpha < byte.MaxValue)
                                hasPartialAlpha = true;
                            pixels[destinationOffset + pixelIndex] = new Color32(
                                rgba[sourceOffset],
                                rgba[sourceOffset + 1],
                                rgba[sourceOffset + 2],
                                alpha
                            );
                            sourceOffset += 4;
                        }
                        expectedFrameIndex++;
                        return true;
                    },
                    cancellationToken
                );
                if (decodeStatus != BasisProfile1SandboxStatus.Success)
                {
                    rejection = callbackMalformed
                        ? BasisProfile1RejectionCategory.Malformed
                        : MapStatus(decodeStatus);
                    error = callbackMalformed
                        ? "Profile 1 full Stage B output disagreed with its validated preflight."
                        : $"Profile 1 full Stage B decode failed: {decodeStatus}.";
                    return false;
                }
                if (expectedFrameIndex != envelope.LogicalFrameCount)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 full Stage B decode returned the wrong logical frame count.";
                    return false;
                }

                if (
                    !BasisAnimatedImageData.TryAdoptNative(
                        envelope.CanvasWidth,
                        envelope.CanvasHeight,
                        envelope.TotalPlayCount,
                        new Color32(0, 0, 0, 0),
                        frames,
                        pixels,
                        frameEnds,
                        checked((long)envelope.BaseTimelineDurationMicroseconds),
                        hasAnyAlpha,
                        hasPartialAlpha,
                        false,
                        out data,
                        out error
                    )
                )
                {
                    rejection = BasisProfile1RejectionCategory.MemoryAdmissionDenied;
                    return false;
                }

                adopted = true;
                return true;
            }
            catch (OutOfMemoryException exception)
            {
                rejection = BasisProfile1RejectionCategory.MemoryAdmissionDenied;
                error = "Profile 1 admitted decode allocation failed: " + exception.Message;
                return false;
            }
            catch (OverflowException exception)
            {
                rejection = BasisProfile1RejectionCategory.MemoryAdmissionDenied;
                error = "Profile 1 admitted decode allocation overflowed: " + exception.Message;
                return false;
            }
            finally
            {
                if (!adopted)
                {
                    if (frames.IsCreated)
                        frames.Dispose();
                    if (pixels.IsCreated)
                        pixels.Dispose();
                    if (frameEnds.IsCreated)
                        frameEnds.Dispose();
                }
            }
        }

        public static BasisProfile1RejectionCategory MapStatus(BasisProfile1SandboxStatus status)
        {
            switch (status)
            {
                case BasisProfile1SandboxStatus.UnsupportedProfile:
                    return BasisProfile1RejectionCategory.UnsupportedProfile;
                case BasisProfile1SandboxStatus.SharedLimitExceeded:
                    return BasisProfile1RejectionCategory.SharedLimitExceeded;
                case BasisProfile1SandboxStatus.Timeout:
                case BasisProfile1SandboxStatus.OutOfFuel:
                    return BasisProfile1RejectionCategory.Timeout;
                case BasisProfile1SandboxStatus.Cancelled:
                    return BasisProfile1RejectionCategory.Cancelled;
                case BasisProfile1SandboxStatus.Malformed:
                case BasisProfile1SandboxStatus.SandboxFailure:
                default:
                    return BasisProfile1RejectionCategory.Malformed;
            }
        }
    }
}
