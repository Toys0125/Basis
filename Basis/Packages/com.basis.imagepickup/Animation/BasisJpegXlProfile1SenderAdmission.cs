using System;

namespace Basis.ImagePickup
{
    internal enum BasisProfile1SenderMemoryClass : byte
    {
        UnsupportedMobile = 0,
        Low = 1,
        Middle = 2,
        High = 3,
    }

    internal readonly struct BasisProfile1SenderPolicy
    {
        public readonly BasisProfile1SenderMemoryClass MemoryClass;
        public readonly bool CanEncodeProfile1;
        public readonly ulong MaximumSubmittedCanvasPixels;
        public readonly long DedicatedEncoderBudgetBytes;
        public readonly long WorkerMemoryCapBytes;
        public readonly int RecommendedThreads;

        public BasisProfile1SenderPolicy(
            BasisProfile1SenderMemoryClass memoryClass,
            bool canEncodeProfile1,
            ulong maximumSubmittedCanvasPixels,
            long dedicatedEncoderBudgetBytes,
            long workerMemoryCapBytes,
            int recommendedThreads
        )
        {
            MemoryClass = memoryClass;
            CanEncodeProfile1 = canEncodeProfile1;
            MaximumSubmittedCanvasPixels = maximumSubmittedCanvasPixels;
            DedicatedEncoderBudgetBytes = dedicatedEncoderBudgetBytes;
            WorkerMemoryCapBytes = workerMemoryCapBytes;
            RecommendedThreads = recommendedThreads;
        }
    }

    internal static class BasisJpegXlProfile1SenderAdmission
    {
        public const int DefaultEffort = 3;
        public const int LatencyFirstEffort = 1;
        public const int ExtendedEffort = 5;
        public const int MaximumNormalThreads = 8;

        private const double EstimateSafetyFactor = 1.20d;
        private const double EstimateFixedOverheadBytes = 76_546_048d;
        private const double EstimateBytesPerSubmittedCanvasPixel = 16.72d;
        private const double EstimateBytesPerAdditionalThread = 1_247_805d;

        private const ulong LowSubmittedPixelLimit = 8UL * 1024UL * 1024UL;
        private const long LowEncoderBudgetBytes = 256L * 1024L * 1024L;
        private const long MiddleEncoderBudgetBytes = 768L * 1024L * 1024L;
        private const long HighEncoderBudgetBytes = 1536L * 1024L * 1024L;

        public static BasisProfile1SenderPolicy GetPolicy(
            int systemMemoryMegabytes,
            bool mobileOrPortablePlatform
        )
        {
            if (mobileOrPortablePlatform)
            {
                return new BasisProfile1SenderPolicy(
                    BasisProfile1SenderMemoryClass.UnsupportedMobile,
                    false,
                    0,
                    0,
                    0,
                    0
                );
            }

            if (systemMemoryMegabytes <= 0 || systemMemoryMegabytes <= 4096)
            {
                return new BasisProfile1SenderPolicy(
                    BasisProfile1SenderMemoryClass.Low,
                    true,
                    LowSubmittedPixelLimit,
                    LowEncoderBudgetBytes,
                    LowEncoderBudgetBytes,
                    2
                );
            }

            if (systemMemoryMegabytes <= 8192)
            {
                return new BasisProfile1SenderPolicy(
                    BasisProfile1SenderMemoryClass.Middle,
                    true,
                    BasisJpegXlProfile1.MaximumSubmittedCanvasPixels,
                    MiddleEncoderBudgetBytes,
                    MiddleEncoderBudgetBytes,
                    4
                );
            }

            return new BasisProfile1SenderPolicy(
                BasisProfile1SenderMemoryClass.High,
                true,
                BasisJpegXlProfile1.MaximumSubmittedCanvasPixels,
                HighEncoderBudgetBytes,
                HighEncoderBudgetBytes,
                4
            );
        }

        public static bool TryEstimateEncoderWorkingSetBytes(
            ulong submittedCanvasPixels,
            int threads,
            out long estimatedBytes
        )
        {
            estimatedBytes = 0;
            if (threads <= 0 || threads > MaximumNormalThreads)
                return false;

            double estimate = EstimateSafetyFactor
                * (
                    EstimateFixedOverheadBytes
                    + submittedCanvasPixels * EstimateBytesPerSubmittedCanvasPixel
                    + (threads - 1) * EstimateBytesPerAdditionalThread
                );
            if (double.IsNaN(estimate) || double.IsInfinity(estimate) || estimate > long.MaxValue)
                return false;

            estimatedBytes = checked((long)Math.Ceiling(estimate));
            return true;
        }

        public static bool TryAdmit(
            int systemMemoryMegabytes,
            bool mobileOrPortablePlatform,
            ulong submittedCanvasPixels,
            int threads,
            out BasisProfile1SenderPolicy policy,
            out long estimatedWorkingSetBytes,
            out string reason
        )
        {
            policy = GetPolicy(systemMemoryMegabytes, mobileOrPortablePlatform);
            estimatedWorkingSetBytes = 0;
            reason = null;

            if (!policy.CanEncodeProfile1)
            {
                reason = "Profile 1 JPEG XL encoding is disabled on mobile/portable senders.";
                return false;
            }
            if (
                submittedCanvasPixels == 0
                || submittedCanvasPixels > BasisJpegXlProfile1.MaximumSubmittedCanvasPixels
                || submittedCanvasPixels > policy.MaximumSubmittedCanvasPixels
            )
            {
                reason = "Profile 1 submitted-canvas pixels exceed this sender memory class.";
                return false;
            }
            if (!TryEstimateEncoderWorkingSetBytes(submittedCanvasPixels, threads, out estimatedWorkingSetBytes))
            {
                reason = "Profile 1 encoder thread count or memory estimate is invalid.";
                return false;
            }
            if (estimatedWorkingSetBytes > policy.DedicatedEncoderBudgetBytes)
            {
                reason = "Profile 1 predicted encoder working set exceeds the dedicated sender budget.";
                return false;
            }
            return true;
        }
    }
}
