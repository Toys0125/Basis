using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace Basis.ImagePickup
{
    internal enum BasisProfile1RejectionCategory : byte
    {
        None = 0,
        Malformed = 1,
        UnsupportedProfile = 2,
        SharedLimitExceeded = 3,
        PayloadLimitExceeded = 4,
        PatchLimitExceeded = 5,
        Timeout = 6,
        Cancelled = 7,
        MemoryAdmissionDenied = 8,
    }

    /// <summary>
    /// Result of the allocation-light Profile 1 container-only trust stage. These values describe
    /// only bytes already bounded by the transport payload; no JPEG XL codestream semantics are
    /// interpreted here.
    /// </summary>
    internal readonly struct BasisProfile1StageAResult
    {
        public readonly int PayloadBytes;
        public readonly int JxlpBoxCount;
        public readonly long ConcatenatedCodestreamBytes;

        public BasisProfile1StageAResult(
            int payloadBytes,
            int jxlpBoxCount,
            long concatenatedCodestreamBytes
        )
        {
            PayloadBytes = payloadBytes;
            JxlpBoxCount = jxlpBoxCount;
            ConcatenatedCodestreamBytes = concatenatedCodestreamBytes;
        }
    }

    /// <summary>
    /// Bounded values the production WASM decoder may return to the host after Stage B semantic
    /// preflight. The host may use only this validated envelope for aggregate memory admission;
    /// it must never parse the remote JPEG XL codestream natively to obtain equivalent values.
    /// </summary>
    internal readonly struct BasisProfile1StageBResourceEnvelope
    {
        public readonly int CanvasWidth;
        public readonly int CanvasHeight;
        public readonly int LogicalFrameCount;
        public readonly uint TotalPlayCount;
        public readonly ulong SubmittedCanvasPixels;
        public readonly ulong BaseTimelineDurationMicroseconds;
        public readonly ulong PublicRegularLayerCount;
        public readonly ulong PublicRegularLayerPixels;
        public readonly ulong CroppedLayerCount;
        public readonly ulong ReferenceReadEdges;
        public readonly ulong SavedReferenceCount;
        public readonly ulong BlendOperationCount;
        public readonly ulong MaximumReferenceChainDepth;
        public readonly ulong PreviewPixels;

        public BasisProfile1StageBResourceEnvelope(
            int canvasWidth,
            int canvasHeight,
            int logicalFrameCount,
            uint totalPlayCount,
            ulong submittedCanvasPixels,
            ulong baseTimelineDurationMicroseconds,
            ulong publicRegularLayerCount,
            ulong publicRegularLayerPixels,
            ulong croppedLayerCount,
            ulong referenceReadEdges,
            ulong savedReferenceCount,
            ulong blendOperationCount,
            ulong maximumReferenceChainDepth,
            ulong previewPixels
        )
        {
            CanvasWidth = canvasWidth;
            CanvasHeight = canvasHeight;
            LogicalFrameCount = logicalFrameCount;
            TotalPlayCount = totalPlayCount;
            SubmittedCanvasPixels = submittedCanvasPixels;
            BaseTimelineDurationMicroseconds = baseTimelineDurationMicroseconds;
            PublicRegularLayerCount = publicRegularLayerCount;
            PublicRegularLayerPixels = publicRegularLayerPixels;
            CroppedLayerCount = croppedLayerCount;
            ReferenceReadEdges = referenceReadEdges;
            SavedReferenceCount = savedReferenceCount;
            BlendOperationCount = blendOperationCount;
            MaximumReferenceChainDepth = maximumReferenceChainDepth;
            PreviewPixels = previewPixels;
        }
    }

    /// <summary>
    /// Profile 1 constants and host-side validation primitives. Remote JPEG XL codestream parsing
    /// intentionally does not live in this type: Stage B belongs to the pinned libjxl WASM sandbox.
    /// </summary>
    internal static class BasisJpegXlProfile1
    {
        public const byte ProfileVersion = 1;
        public const int MaximumWidth = 2048;
        public const int MaximumHeight = 2048;
        public const ulong MaximumCanvasPixels = 4_194_304UL;
        public const int MaximumLogicalFrames = 512;
        public const ulong MaximumSubmittedCanvasPixels = 33_554_432UL;
        public const int MaximumPayloadBytes = 64 * 1024 * 1024;
        public const long MinimumFrameDurationMicroseconds = 33_334L;
        public const ulong MaximumBaseTimelineDurationMicroseconds = 300_000_000UL;
        public const uint TimebaseNumerator = 1_000_000U;
        public const uint TimebaseDenominator = 1U;

        private const uint JxlpFinalMarker = 0x80000000U;
        private const uint JxlpSequenceMask = 0x7fffffffU;
        private const int ContainerPrefixBytes = 32;
        private const int BoxHeaderBytes = 8;
        private const int JxlpCounterBytes = 4;
        private const int MinimumJxlpBoxBytes = BoxHeaderBytes + JxlpCounterBytes;

        private static readonly byte[] ContainerSignature =
        {
            0x00, 0x00, 0x00, 0x0c, 0x4a, 0x58, 0x4c, 0x20,
            0x0d, 0x0a, 0x87, 0x0a,
        };

        private static readonly byte[] ExactFtyp =
        {
            0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
            0x6a, 0x78, 0x6c, 0x20, 0x00, 0x00, 0x00, 0x00,
            0x6a, 0x78, 0x6c, 0x20,
        };

        public static bool TryValidateStageA(
            NativeArray<byte> payload,
            int declaredLength,
            byte profileVersion,
            out BasisProfile1StageAResult result,
            out BasisProfile1RejectionCategory rejection,
            out string error
        )
        {
            result = default;
            rejection = BasisProfile1RejectionCategory.None;
            error = null;

            if (profileVersion != ProfileVersion)
            {
                rejection = BasisProfile1RejectionCategory.UnsupportedProfile;
                error = $"Unsupported animated-image profile version {profileVersion}.";
                return false;
            }

            if (declaredLength < 0)
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 declared payload length is invalid.";
                return false;
            }
            if (declaredLength > MaximumPayloadBytes)
            {
                rejection = BasisProfile1RejectionCategory.PayloadLimitExceeded;
                error = "Profile 1 payload exceeds the 64 MiB encoded-payload limit.";
                return false;
            }

            if (!payload.IsCreated || payload.Length != declaredLength)
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 declared payload length does not match the reassembled bytes.";
                return false;
            }

            if (declaredLength < ContainerPrefixBytes)
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 JPEG XL container is truncated before its required prefix.";
                return false;
            }

            if (!Matches(payload, 0, ContainerSignature))
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 JPEG XL container signature is invalid.";
                return false;
            }

            if (!Matches(payload, ContainerSignature.Length, ExactFtyp))
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 JPEG XL ftyp box is not canonical.";
                return false;
            }

            int offset = ContainerPrefixBytes;
            ulong expectedSequence = 0;
            int boxCount = 0;
            long codestreamBytes = 0;
            bool sawFinal = false;

            while (offset < declaredLength)
            {
                if (declaredLength - offset < BoxHeaderBytes)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 JPEG XL container has a truncated box header.";
                    return false;
                }

                uint boxSize = ReadUInt32BigEndian(payload, offset);
                if (boxSize < MinimumJxlpBoxBytes || boxSize > int.MaxValue)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 JPEG XL box size is invalid or unsupported.";
                    return false;
                }

                long boxEndLong = (long)offset + boxSize;
                if (boxEndLong > declaredLength)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 JPEG XL jxlp box extends beyond the payload.";
                    return false;
                }

                if (!IsJxlp(payload, offset + 4))
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 permits only ordered jxlp boxes after the exact ftyp box.";
                    return false;
                }

                uint counter = ReadUInt32BigEndian(payload, offset + BoxHeaderBytes);
                uint sequence = counter & JxlpSequenceMask;
                bool isFinal = (counter & JxlpFinalMarker) != 0;
                if (expectedSequence > JxlpSequenceMask || sequence != expectedSequence)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 jxlp sequence numbers must start at zero and be consecutive.";
                    return false;
                }

                int boxEnd = (int)boxEndLong;
                long boxCodestreamBytes = boxSize - MinimumJxlpBoxBytes;
                if (codestreamBytes > long.MaxValue - boxCodestreamBytes)
                {
                    rejection = BasisProfile1RejectionCategory.Malformed;
                    error = "Profile 1 concatenated codestream byte count overflowed.";
                    return false;
                }
                codestreamBytes += boxCodestreamBytes;
                boxCount++;
                expectedSequence++;

                if (isFinal)
                {
                    sawFinal = true;
                    if (boxEnd != declaredLength)
                    {
                        rejection = BasisProfile1RejectionCategory.Malformed;
                        error = "Profile 1 contains a box or trailing data after the final jxlp box.";
                        return false;
                    }
                    offset = boxEnd;
                    break;
                }

                offset = boxEnd;
            }

            if (!sawFinal || boxCount == 0 || offset != declaredLength)
            {
                rejection = BasisProfile1RejectionCategory.Malformed;
                error = "Profile 1 JPEG XL container is missing its final-marked jxlp box.";
                return false;
            }

            result = new BasisProfile1StageAResult(declaredLength, boxCount, codestreamBytes);
            return true;
        }

        public static bool TryCalculateSubmittedCanvasPixels(
            ulong canvasWidth,
            ulong canvasHeight,
            ulong logicalFrameCount,
            out ulong submittedCanvasPixels
        )
        {
            submittedCanvasPixels = 0;
            if (!TryMultiply(canvasWidth, canvasHeight, out ulong canvasPixels))
                return false;
            return TryMultiply(canvasPixels, logicalFrameCount, out submittedCanvasPixels);
        }

        public static bool TryCalculateBaseTimelineDurationMicroseconds(
            IReadOnlyList<long> frameDurationsMicroseconds,
            out ulong baseTimelineDurationMicroseconds
        )
        {
            baseTimelineDurationMicroseconds = 0;
            if (frameDurationsMicroseconds == null)
                return false;

            int count = frameDurationsMicroseconds.Count;
            for (int i = 0; i < count; i++)
            {
                long duration = frameDurationsMicroseconds[i];
                if (duration < 0)
                    return false;
                ulong unsignedDuration = (ulong)duration;
                if (baseTimelineDurationMicroseconds > ulong.MaxValue - unsignedDuration)
                {
                    baseTimelineDurationMicroseconds = 0;
                    return false;
                }
                baseTimelineDurationMicroseconds += unsignedDuration;
            }
            return true;
        }

        /// <summary>
        /// Validates values already obtained by the sandboxed JPEG XL semantic preflight. This is
        /// intentionally not a codestream parser; it is the host-side invariant check before memory
        /// admission and full output decode.
        /// </summary>
        public static bool TryValidateStageBEnvelope(
            int canvasWidth,
            int canvasHeight,
            int logicalFrameCount,
            IReadOnlyList<long> frameDurationsMicroseconds,
            uint timebaseNumerator,
            uint timebaseDenominator,
            out ulong submittedCanvasPixels,
            out ulong baseTimelineDurationMicroseconds,
            out BasisProfile1RejectionCategory rejection,
            out string error
        )
        {
            submittedCanvasPixels = 0;
            baseTimelineDurationMicroseconds = 0;
            rejection = BasisProfile1RejectionCategory.None;
            error = null;

            if (timebaseNumerator != TimebaseNumerator || timebaseDenominator != TimebaseDenominator)
            {
                rejection = BasisProfile1RejectionCategory.UnsupportedProfile;
                error = "Profile 1 requires the exact 1,000,000 / 1 animation timebase.";
                return false;
            }

            if (
                canvasWidth <= 0
                || canvasHeight <= 0
                || canvasWidth > MaximumWidth
                || canvasHeight > MaximumHeight
            )
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 canvas dimensions are outside the shared limits.";
                return false;
            }

            if (
                !TryMultiply((ulong)canvasWidth, (ulong)canvasHeight, out ulong canvasPixels)
                || canvasPixels > MaximumCanvasPixels
            )
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 canvas pixel count exceeds the shared limit.";
                return false;
            }

            if (
                logicalFrameCount <= 0
                || logicalFrameCount > MaximumLogicalFrames
                || frameDurationsMicroseconds == null
                || frameDurationsMicroseconds.Count != logicalFrameCount
            )
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 logical frame count is outside the shared limit.";
                return false;
            }

            if (
                !TryCalculateSubmittedCanvasPixels(
                    (ulong)canvasWidth,
                    (ulong)canvasHeight,
                    (ulong)logicalFrameCount,
                    out submittedCanvasPixels
                )
            )
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 submitted-canvas pixel arithmetic overflowed.";
                return false;
            }
            if (submittedCanvasPixels > MaximumSubmittedCanvasPixels)
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 submitted-canvas pixel count exceeds 32 Mi-pixels.";
                return false;
            }

            for (int i = 0; i < logicalFrameCount; i++)
            {
                if (frameDurationsMicroseconds[i] < MinimumFrameDurationMicroseconds)
                {
                    rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                    error = "Profile 1 frame duration is below 33,334 microseconds.";
                    return false;
                }
            }

            if (
                !TryCalculateBaseTimelineDurationMicroseconds(
                    frameDurationsMicroseconds,
                    out baseTimelineDurationMicroseconds
                )
            )
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 base-timeline arithmetic overflowed.";
                return false;
            }
            if (
                baseTimelineDurationMicroseconds == 0
                || baseTimelineDurationMicroseconds > MaximumBaseTimelineDurationMicroseconds
            )
            {
                rejection = BasisProfile1RejectionCategory.SharedLimitExceeded;
                error = "Profile 1 base timeline exceeds the shared duration limit.";
                return false;
            }

            return true;
        }

        private static bool TryMultiply(ulong left, ulong right, out ulong result)
        {
            result = 0;
            if (left != 0 && right > ulong.MaxValue / left)
                return false;
            result = left * right;
            return true;
        }

        private static bool Matches(NativeArray<byte> payload, int offset, byte[] expected)
        {
            if (offset < 0 || expected == null || offset > payload.Length - expected.Length)
                return false;
            for (int i = 0; i < expected.Length; i++)
            {
                if (payload[offset + i] != expected[i])
                    return false;
            }
            return true;
        }

        private static bool IsJxlp(NativeArray<byte> payload, int offset)
        {
            return offset >= 0
                && offset <= payload.Length - 4
                && payload[offset] == (byte)'j'
                && payload[offset + 1] == (byte)'x'
                && payload[offset + 2] == (byte)'l'
                && payload[offset + 3] == (byte)'p';
        }

        private static uint ReadUInt32BigEndian(NativeArray<byte> payload, int offset)
        {
            return ((uint)payload[offset] << 24)
                | ((uint)payload[offset + 1] << 16)
                | ((uint)payload[offset + 2] << 8)
                | payload[offset + 3];
        }
    }

    /// <summary>
    /// Deterministic conversion boundary from complete Profile 1 logical RGBA8 canvases to Basis
    /// runtime patches. Each logical frame is one full-canvas Source/None patch, so extracted patch
    /// pixels equal submitted canvas pixels exactly and the inflation factor is 1.0.
    /// </summary>
    internal static class BasisProfile1PatchConverter
    {
        public static bool TryCreate(
            int canvasWidth,
            int canvasHeight,
            uint totalPlayCount,
            IReadOnlyList<long> frameDurationsMicroseconds,
            IReadOnlyList<Color32[]> logicalCanvases,
            out BasisAnimatedImageData data,
            out string error
        )
        {
            data = null;
            error = null;
            int frameCount = logicalCanvases?.Count ?? 0;
            if (
                !BasisJpegXlProfile1.TryValidateStageBEnvelope(
                    canvasWidth,
                    canvasHeight,
                    frameCount,
                    frameDurationsMicroseconds,
                    BasisJpegXlProfile1.TimebaseNumerator,
                    BasisJpegXlProfile1.TimebaseDenominator,
                    out ulong submittedCanvasPixels,
                    out _,
                    out _,
                    out error
                )
            )
            {
                return false;
            }

            if (submittedCanvasPixels > int.MaxValue)
            {
                error = "Profile 1 patch conversion exceeds the runtime addressable pixel pool.";
                return false;
            }

            int canvasPixels = checked(canvasWidth * canvasHeight);
            var frames = new BasisAnimatedImageFrameSource[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                Color32[] canvas = logicalCanvases[i];
                if (canvas == null || canvas.Length != canvasPixels)
                {
                    error = $"Profile 1 logical canvas {i + 1:N0} does not match the declared canvas.";
                    return false;
                }
                frames[i] = new BasisAnimatedImageFrameSource(
                    new RectInt(0, 0, canvasWidth, canvasHeight),
                    frameDurationsMicroseconds[i],
                    BasisAnimationBlend.Source,
                    BasisAnimationDisposal.None,
                    canvas
                );
            }

            return BasisAnimatedImageData.TryCreate(
                canvasWidth,
                canvasHeight,
                totalPlayCount,
                new Color32(0, 0, 0, 0),
                frames,
                out data,
                out error
            );
        }
    }
}
