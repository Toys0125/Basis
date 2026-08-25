using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Basis.ImageSandbox
{
    public enum BasisProfile1SandboxStatus : byte
    {
        Success = 0,
        Malformed = 1,
        UnsupportedProfile = 2,
        SharedLimitExceeded = 3,
        Timeout = 6,
        Cancelled = 7,
        OutOfFuel = 8,
        SandboxFailure = 255,
    }

    public enum BasisProfile1SandboxDiagnosticReason : byte
    {
        None = 0,
        Dimensions = 1,
        CanvasPixels = 2,
        BitsPerSample = 3,
        ColorChannels = 4,
        ExtraChannels = 5,
        Alpha = 6,
        PremultipliedAlpha = 7,
        Orientation = 8,
        ExtraChannel = 9,
        MissingAnimation = 10,
        Timebase = 11,
        ColorEncoding = 12,
        LogicalFrames = 13,
        FrameDuration = 14,
        Timeline = 15,
        SubmittedPixels = 16,
        StructuralLayerPixels = 17,
        StructuralLayerCount = 18,
        ReferenceSource = 19,
        PreviewPixels = 20,
        LogicalMismatch = 21,
        Decoder = 22,
        DecodeWorkOverflow = 23,
    }

    public readonly struct BasisProfile1SandboxExecutionMetrics
    {
        public readonly ulong InitialMemoryBytes;
        public readonly ulong PeakMemoryBytes;
        public readonly ulong FinalMemoryBytes;
        public readonly int MemoryGrowthCount;

        internal BasisProfile1SandboxExecutionMetrics(
            ulong initialMemoryBytes,
            ulong peakMemoryBytes,
            ulong finalMemoryBytes,
            int memoryGrowthCount)
        {
            InitialMemoryBytes = initialMemoryBytes;
            PeakMemoryBytes = peakMemoryBytes;
            FinalMemoryBytes = finalMemoryBytes;
            MemoryGrowthCount = memoryGrowthCount;
        }
    }

    public readonly struct BasisProfile1SandboxPreflightMetrics
    {
        public readonly double LogicalHeaderMilliseconds;
        public readonly double StructuralHeaderMilliseconds;
        public readonly double ValidationMilliseconds;
        public readonly ulong LogicalHeaderFuelConsumed;
        public readonly ulong StructuralHeaderFuelConsumed;
        public readonly ulong ValidationFuelConsumed;
        public readonly bool FuelConsumedAvailable;
        public readonly BasisProfile1SandboxExecutionMetrics Execution;

        public double HeaderMilliseconds => LogicalHeaderMilliseconds + StructuralHeaderMilliseconds;
        public ulong HeaderFuelConsumed => LogicalHeaderFuelConsumed + StructuralHeaderFuelConsumed;

        internal BasisProfile1SandboxPreflightMetrics(
            double logicalHeaderMilliseconds,
            double structuralHeaderMilliseconds,
            double validationMilliseconds,
            ulong logicalHeaderFuelConsumed,
            ulong structuralHeaderFuelConsumed,
            ulong validationFuelConsumed,
            bool fuelConsumedAvailable,
            BasisProfile1SandboxExecutionMetrics execution)
        {
            LogicalHeaderMilliseconds = logicalHeaderMilliseconds;
            StructuralHeaderMilliseconds = structuralHeaderMilliseconds;
            ValidationMilliseconds = validationMilliseconds;
            LogicalHeaderFuelConsumed = logicalHeaderFuelConsumed;
            StructuralHeaderFuelConsumed = structuralHeaderFuelConsumed;
            ValidationFuelConsumed = validationFuelConsumed;
            FuelConsumedAvailable = fuelConsumedAvailable;
            Execution = execution;
        }
    }

    public readonly struct BasisProfile1SandboxLimits
    {
        public readonly long MaximumLinearMemoryBytes;
        public readonly ulong Fuel;
        public readonly TimeSpan Timeout;

        public BasisProfile1SandboxLimits(
            long maximumLinearMemoryBytes,
            ulong fuel,
            TimeSpan timeout
        )
        {
            if (maximumLinearMemoryBytes < 32L * 1024L * 1024L)
                throw new ArgumentOutOfRangeException(nameof(maximumLinearMemoryBytes));
            if (fuel == 0)
                throw new ArgumentOutOfRangeException(nameof(fuel));
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));

            MaximumLinearMemoryBytes = maximumLinearMemoryBytes;
            Fuel = fuel;
            Timeout = timeout;
        }
    }

    public readonly struct BasisProfile1SandboxPreflight
    {
        public readonly BasisProfile1SandboxStatus Status;
        public readonly BasisProfile1SandboxDiagnosticReason DiagnosticReason;
        public readonly uint Width;
        public readonly uint Height;
        public readonly uint LogicalFrameCount;
        public readonly uint TotalPlayCount;
        public readonly ulong SubmittedCanvasPixels;
        public readonly ulong BaseTimelineMicroseconds;
        public readonly ulong PublicRegularLayerCount;
        public readonly ulong PublicRegularLayerPixels;
        public readonly ulong CroppedLayerCount;
        public readonly ulong ReferenceReadEdges;
        public readonly ulong SavedReferenceCount;
        public readonly ulong BlendOperationCount;
        public readonly ulong MaximumReferenceChainDepth;
        public readonly ulong PreviewPixels;
        public readonly ulong CroppedLayerPixels;
        public readonly ulong ReferenceReadPixels;
        public readonly ulong SavedReferencePixels;
        public readonly ulong BlendOperationPixels;
        public readonly ulong ReferenceChainExtraPixels;
        public readonly ulong DecodeWorkCandidate;
        public readonly ulong[] FrameDurationsMicroseconds;

        public ulong CodedFrameCount => PublicRegularLayerCount;
        public ulong CodedFramePixels => PublicRegularLayerPixels;

        internal BasisProfile1SandboxPreflight(
            BasisProfile1SandboxStatus status,
            BasisProfile1SandboxDiagnosticReason diagnosticReason = BasisProfile1SandboxDiagnosticReason.None,
            uint width = 0,
            uint height = 0,
            uint logicalFrameCount = 0,
            uint totalPlayCount = 0,
            ulong submittedCanvasPixels = 0,
            ulong baseTimelineMicroseconds = 0,
            ulong publicRegularLayerCount = 0,
            ulong publicRegularLayerPixels = 0,
            ulong croppedLayerCount = 0,
            ulong referenceReadEdges = 0,
            ulong savedReferenceCount = 0,
            ulong blendOperationCount = 0,
            ulong maximumReferenceChainDepth = 0,
            ulong previewPixels = 0,
            ulong croppedLayerPixels = 0,
            ulong referenceReadPixels = 0,
            ulong savedReferencePixels = 0,
            ulong blendOperationPixels = 0,
            ulong referenceChainExtraPixels = 0,
            ulong decodeWorkCandidate = 0,
            ulong[] frameDurationsMicroseconds = null
        )
        {
            Status = status;
            DiagnosticReason = diagnosticReason;
            Width = width;
            Height = height;
            LogicalFrameCount = logicalFrameCount;
            TotalPlayCount = totalPlayCount;
            SubmittedCanvasPixels = submittedCanvasPixels;
            BaseTimelineMicroseconds = baseTimelineMicroseconds;
            PublicRegularLayerCount = publicRegularLayerCount;
            PublicRegularLayerPixels = publicRegularLayerPixels;
            CroppedLayerCount = croppedLayerCount;
            ReferenceReadEdges = referenceReadEdges;
            SavedReferenceCount = savedReferenceCount;
            BlendOperationCount = blendOperationCount;
            MaximumReferenceChainDepth = maximumReferenceChainDepth;
            PreviewPixels = previewPixels;
            CroppedLayerPixels = croppedLayerPixels;
            ReferenceReadPixels = referenceReadPixels;
            SavedReferencePixels = savedReferencePixels;
            BlendOperationPixels = blendOperationPixels;
            ReferenceChainExtraPixels = referenceChainExtraPixels;
            DecodeWorkCandidate = decodeWorkCandidate;
            FrameDurationsMicroseconds = frameDurationsMicroseconds ?? Array.Empty<ulong>();
        }
    }

    public delegate bool BasisProfile1DecodedFrameConsumer(
        int frameIndex,
        byte[] reusableRgba8Canvas,
        ulong durationMicroseconds
    );

    /// <summary>
    /// Hosts the pinned Profile 1 JPEG XL decoder inside Wasmtime. The decoder
    /// module has one validated no-op host import for Emscripten memory-growth
    /// notification and no WASI, filesystem, networking, clock, or random imports.
    /// One runtime object executes one sandbox call at a time so epoch-based
    /// cancellation cannot interrupt an unrelated decode.
    /// </summary>
    public sealed class BasisProfile1SandboxDecoder : IDisposable
    {
        public const string LibJxlVersion = "0.12.0";
        public const string LibJxlCommit = "a7a9c787341cf703dede03c2009fa460cae5e5df";
        public const string EmscriptenVersion = "4.0.23";
        public const string WasmtimeVersion = "44.0.0";
        public const string NativeRuntimeSourceCommit = "f200256a2e56c1c5229a07e5530faa4a6b1ab325";

        private const uint DecoderAbiVersion = 1;
        private const int MaximumFrames = 512;
        private const int ResultHeaderSlots = 17;
        private const int DiagnosticReasonSlot = ResultHeaderSlots + MaximumFrames;
        private const int CroppedLayerPixelsSlot = DiagnosticReasonSlot + 1;
        private const int ReferenceReadPixelsSlot = CroppedLayerPixelsSlot + 1;
        private const int SavedReferencePixelsSlot = ReferenceReadPixelsSlot + 1;
        private const int BlendOperationPixelsSlot = SavedReferencePixelsSlot + 1;
        private const int ReferenceChainExtraPixelsSlot = BlendOperationPixelsSlot + 1;
        private const int DecodeWorkCandidateSlot = ReferenceChainExtraPixelsSlot + 1;
        private const int ExpectedResultSlots = DecodeWorkCandidateSlot + 1;
        private const uint DecodeEndOfStream = 4;

        private static readonly WasmtimeNative.HostFunctionCallback MemoryGrowthCallback =
            IgnoreMemoryGrowthNotification;

        private readonly object _gate = new object();
        private readonly BasisProfile1SandboxLimits _limits;
        private IntPtr _engine;
        private IntPtr _module;
        private bool _disposed;

        public BasisProfile1SandboxDecoder(
            byte[] wasmModule,
            BasisProfile1SandboxLimits limits
        )
        {
            if (wasmModule == null || wasmModule.Length == 0)
                throw new ArgumentException("Profile 1 decoder WASM is required.", nameof(wasmModule));

            _limits = limits;
            InitializeEngineAndModule(wasmModule);
            ValidateImportContract();
        }

        public BasisProfile1SandboxPreflight Preflight(
            byte[] canonicalProfile1Container,
            CancellationToken cancellationToken = default
        ) => Preflight(canonicalProfile1Container, out _, out _, cancellationToken);

        public BasisProfile1SandboxPreflight Preflight(
            byte[] canonicalProfile1Container,
            out ulong fuelConsumed,
            out bool fuelConsumedAvailable,
            CancellationToken cancellationToken = default
        )
        {
            fuelConsumed = 0;
            fuelConsumedAvailable = false;
            if (canonicalProfile1Container == null || canonicalProfile1Container.Length == 0)
                return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.Malformed);

            lock (_gate)
            {
                ThrowIfDisposed();
                using var timeout = CreateTimeout(cancellationToken);
                CancellationToken effectiveToken = timeout.Token;
                if (effectiveToken.IsCancellationRequested)
                    return new BasisProfile1SandboxPreflight(ResolveCancellationStatus(cancellationToken));

                using var epochRegistration = effectiveToken.Register(
                    static state =>
                        WasmtimeNative.wasmtime_engine_increment_epoch((IntPtr)state),
                    _engine
                );

                using var instance = CreateInstance();
                if (!instance.IsValid)
                    return new BasisProfile1SandboxPreflight(instance.FailureStatus);

                int inputPointer = 0;
                int resultPointer = 0;
                try
                {
                    if (
                        !TryAllocateAndCopy(
                            instance,
                            canonicalProfile1Container,
                            out inputPointer,
                            out BasisProfile1SandboxStatus allocationStatus
                        )
                    )
                    {
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(
                                allocationStatus,
                                cancellationToken,
                                timeout
                            )
                        );
                    }

                    if (!TryCallI32(instance, "p1_result_u64_count", null, out int resultSlots, out BasisProfile1SandboxStatus callStatus))
                        return new BasisProfile1SandboxPreflight(callStatus);
                    if (resultSlots != ExpectedResultSlots)
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);

                    int resultBytes = checked(resultSlots * sizeof(ulong));
                    if (
                        !TryAllocate(
                            instance,
                            resultBytes,
                            out resultPointer,
                            out allocationStatus
                        )
                    )
                    {
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(
                                allocationStatus,
                                cancellationToken,
                                timeout
                            )
                        );
                    }

                    var args = new[]
                    {
                        WasmtimeNative.WasmtimeValue.I32(inputPointer),
                        WasmtimeNative.WasmtimeValue.I32(canonicalProfile1Container.Length),
                        WasmtimeNative.WasmtimeValue.I32(resultPointer),
                    };
                    if (!TryCallI32(instance, "p1_preflight", args, out int nativeStatus, out callStatus))
                        return new BasisProfile1SandboxPreflight(ResolveInterruptedStatus(callStatus, cancellationToken, timeout));

                    var status = MapDecoderStatus((uint)nativeStatus);
                    if (!TryReadResult(instance, resultPointer, out BasisProfile1SandboxPreflight result))
                        return new BasisProfile1SandboxPreflight(status == BasisProfile1SandboxStatus.Success
                            ? BasisProfile1SandboxStatus.SandboxFailure
                            : status);
                    return result.Status == status
                        ? result
                        : new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                }
                finally
                {
                    fuelConsumedAvailable = TryGetFuelConsumed(instance, out fuelConsumed);
                    if (resultPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(resultPointer));
                    if (inputPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(inputPointer));
                }
            }
        }

        public BasisProfile1SandboxPreflight PreflightDetailed(
            byte[] canonicalProfile1Container,
            out BasisProfile1SandboxPreflightMetrics metrics,
            CancellationToken cancellationToken = default
        )
        {
            metrics = default;
            if (canonicalProfile1Container == null || canonicalProfile1Container.Length == 0)
                return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.Malformed);

            lock (_gate)
            {
                ThrowIfDisposed();
                using var timeout = CreateTimeout(cancellationToken);
                CancellationToken effectiveToken = timeout.Token;
                if (effectiveToken.IsCancellationRequested)
                    return new BasisProfile1SandboxPreflight(ResolveCancellationStatus(cancellationToken));

                using var epochRegistration = effectiveToken.Register(
                    static state => WasmtimeNative.wasmtime_engine_increment_epoch((IntPtr)state),
                    _engine
                );
                using var instance = CreateInstance();
                if (!instance.IsValid)
                    return new BasisProfile1SandboxPreflight(instance.FailureStatus);

                int inputPointer = 0;
                int resultPointer = 0;
                double logicalHeaderMilliseconds = 0;
                double structuralHeaderMilliseconds = 0;
                double validationMilliseconds = 0;
                ulong logicalHeaderFuel = 0;
                ulong structuralHeaderFuel = 0;
                ulong validationFuel = 0;
                bool logicalHeaderFuelAvailable = false;
                bool structuralHeaderFuelAvailable = false;
                bool validationFuelAvailable = false;
                ulong initialMemory = GetMemorySizeBytes(instance);
                ulong peakMemory = initialMemory;
                try
                {
                    if (!TryAllocateAndCopy(
                            instance,
                            canonicalProfile1Container,
                            out inputPointer,
                            out BasisProfile1SandboxStatus allocationStatus))
                    {
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(allocationStatus, cancellationToken, timeout));
                    }
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));

                    if (!TryCallI32(instance, "p1_result_u64_count", null, out int resultSlots, out BasisProfile1SandboxStatus callStatus))
                        return new BasisProfile1SandboxPreflight(callStatus);
                    if (resultSlots != ExpectedResultSlots)
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);

                    if (!TryAllocate(instance, checked(resultSlots * sizeof(ulong)), out resultPointer, out allocationStatus))
                    {
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(allocationStatus, cancellationToken, timeout));
                    }
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));

                    var args = new[]
                    {
                        WasmtimeNative.WasmtimeValue.I32(inputPointer),
                        WasmtimeNative.WasmtimeValue.I32(canonicalProfile1Container.Length),
                        WasmtimeNative.WasmtimeValue.I32(resultPointer),
                    };

                    bool hadLogicalFuel = TryGetFuelRemaining(instance, out ulong beforeLogicalFuel);
                    var stopwatch = Stopwatch.StartNew();
                    bool logicalCallOk = TryCallI32(
                        instance,
                        "p1_preflight_logical_headers",
                        args,
                        out int logicalNativeStatus,
                        out callStatus
                    );
                    stopwatch.Stop();
                    logicalHeaderMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));
                    bool hasLogicalAfterFuel = TryGetFuelRemaining(instance, out ulong afterLogicalFuel);
                    logicalHeaderFuelAvailable = hadLogicalFuel && hasLogicalAfterFuel && beforeLogicalFuel >= afterLogicalFuel;
                    if (logicalHeaderFuelAvailable)
                        logicalHeaderFuel = beforeLogicalFuel - afterLogicalFuel;
                    if (!logicalCallOk)
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(callStatus, cancellationToken, timeout));

                    BasisProfile1SandboxStatus logicalStatus = MapDecoderStatus((uint)logicalNativeStatus);
                    if (!TryReadResult(instance, resultPointer, out BasisProfile1SandboxPreflight logicalResult))
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                    if (logicalResult.Status != logicalStatus)
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                    if (logicalStatus != BasisProfile1SandboxStatus.Success)
                        return logicalResult;

                    bool hadStructuralFuel = TryGetFuelRemaining(instance, out ulong beforeStructuralFuel);
                    stopwatch.Restart();
                    bool structuralCallOk = TryCallI32(
                        instance,
                        "p1_preflight_structural_headers",
                        args,
                        out int structuralNativeStatus,
                        out callStatus
                    );
                    stopwatch.Stop();
                    structuralHeaderMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));
                    bool hasStructuralAfterFuel = TryGetFuelRemaining(instance, out ulong afterStructuralFuel);
                    structuralHeaderFuelAvailable = hadStructuralFuel && hasStructuralAfterFuel && beforeStructuralFuel >= afterStructuralFuel;
                    if (structuralHeaderFuelAvailable)
                        structuralHeaderFuel = beforeStructuralFuel - afterStructuralFuel;
                    if (!structuralCallOk)
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(callStatus, cancellationToken, timeout));

                    BasisProfile1SandboxStatus structuralStatus = MapDecoderStatus((uint)structuralNativeStatus);
                    if (!TryReadResult(instance, resultPointer, out BasisProfile1SandboxPreflight structuralResult))
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                    if (structuralResult.Status != structuralStatus)
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                    if (structuralStatus != BasisProfile1SandboxStatus.Success)
                        return structuralResult;

                    bool hadValidationFuel = TryGetFuelRemaining(instance, out ulong beforeValidationFuel);
                    stopwatch.Restart();
                    bool validationCallOk = TryCallI32(
                        instance,
                        "p1_preflight_validate",
                        args,
                        out int validationNativeStatus,
                        out callStatus
                    );
                    stopwatch.Stop();
                    validationMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));
                    bool hasValidationAfterFuel = TryGetFuelRemaining(instance, out ulong afterValidationFuel);
                    validationFuelAvailable = hadValidationFuel && hasValidationAfterFuel && beforeValidationFuel >= afterValidationFuel;
                    if (validationFuelAvailable)
                        validationFuel = beforeValidationFuel - afterValidationFuel;
                    if (!validationCallOk)
                        return new BasisProfile1SandboxPreflight(
                            ResolveInterruptedStatus(callStatus, cancellationToken, timeout));

                    BasisProfile1SandboxStatus validationStatus = MapDecoderStatus((uint)validationNativeStatus);
                    if (!TryReadResult(instance, resultPointer, out BasisProfile1SandboxPreflight result))
                        return new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                    return result.Status == validationStatus
                        ? result
                        : new BasisProfile1SandboxPreflight(BasisProfile1SandboxStatus.SandboxFailure);
                }
                finally
                {
                    ulong finalMemory = GetMemorySizeBytes(instance);
                    peakMemory = Math.Max(peakMemory, finalMemory);
                    metrics = new BasisProfile1SandboxPreflightMetrics(
                        logicalHeaderMilliseconds,
                        structuralHeaderMilliseconds,
                        validationMilliseconds,
                        logicalHeaderFuel,
                        structuralHeaderFuel,
                        validationFuel,
                        logicalHeaderFuelAvailable || structuralHeaderFuelAvailable || validationFuelAvailable,
                        new BasisProfile1SandboxExecutionMetrics(
                            initialMemory,
                            peakMemory,
                            finalMemory,
                            instance.MemoryGrowthCount
                        )
                    );
                    if (resultPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(resultPointer));
                    if (inputPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(inputPointer));
                }
            }
        }

        /// <summary>
        /// Performs the admitted full pixel pass. The frame byte array is reused for
        /// every callback and is valid only for the duration of the callback.
        /// </summary>
        public BasisProfile1SandboxStatus DecodeFrames(
            byte[] canonicalProfile1Container,
            BasisProfile1SandboxPreflight preflight,
            BasisProfile1DecodedFrameConsumer consumer,
            CancellationToken cancellationToken = default
        ) => DecodeFramesCore(
            canonicalProfile1Container,
            preflight,
            consumer,
            out _,
            out _,
            out _,
            cancellationToken
        );

        public BasisProfile1SandboxStatus DecodeFrames(
            byte[] canonicalProfile1Container,
            BasisProfile1SandboxPreflight preflight,
            BasisProfile1DecodedFrameConsumer consumer,
            out ulong fuelConsumed,
            out bool fuelConsumedAvailable,
            CancellationToken cancellationToken = default
        ) => DecodeFramesCore(
            canonicalProfile1Container,
            preflight,
            consumer,
            out fuelConsumed,
            out fuelConsumedAvailable,
            out _,
            cancellationToken
        );

        public BasisProfile1SandboxStatus DecodeFramesDetailed(
            byte[] canonicalProfile1Container,
            BasisProfile1SandboxPreflight preflight,
            BasisProfile1DecodedFrameConsumer consumer,
            out ulong fuelConsumed,
            out bool fuelConsumedAvailable,
            out BasisProfile1SandboxExecutionMetrics executionMetrics,
            CancellationToken cancellationToken = default
        ) => DecodeFramesCore(
            canonicalProfile1Container,
            preflight,
            consumer,
            out fuelConsumed,
            out fuelConsumedAvailable,
            out executionMetrics,
            cancellationToken
        );

        private BasisProfile1SandboxStatus DecodeFramesCore(
            byte[] canonicalProfile1Container,
            BasisProfile1SandboxPreflight preflight,
            BasisProfile1DecodedFrameConsumer consumer,
            out ulong fuelConsumed,
            out bool fuelConsumedAvailable,
            out BasisProfile1SandboxExecutionMetrics executionMetrics,
            CancellationToken cancellationToken
        )
        {
            fuelConsumed = 0;
            fuelConsumedAvailable = false;
            executionMetrics = default;
            if (
                canonicalProfile1Container == null
                || canonicalProfile1Container.Length == 0
                || preflight.Status != BasisProfile1SandboxStatus.Success
                || preflight.Width == 0
                || preflight.Height == 0
                || preflight.LogicalFrameCount == 0
                || consumer == null
            )
            {
                return BasisProfile1SandboxStatus.Malformed;
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                using var timeout = CreateTimeout(cancellationToken);
                CancellationToken effectiveToken = timeout.Token;
                if (effectiveToken.IsCancellationRequested)
                    return ResolveCancellationStatus(cancellationToken);

                using var epochRegistration = effectiveToken.Register(
                    static state =>
                        WasmtimeNative.wasmtime_engine_increment_epoch((IntPtr)state),
                    _engine
                );

                using var instance = CreateInstance();
                if (!instance.IsValid)
                    return instance.FailureStatus;

                int inputPointer = 0;
                int outputPointer = 0;
                int durationPointer = 0;
                bool sessionOpen = false;
                ulong initialMemory = GetMemorySizeBytes(instance);
                ulong peakMemory = initialMemory;
                try
                {
                    if (
                        !TryAllocateAndCopy(
                            instance,
                            canonicalProfile1Container,
                            out inputPointer,
                            out BasisProfile1SandboxStatus allocationStatus
                        )
                    )
                    {
                        return ResolveInterruptedStatus(
                            allocationStatus,
                            cancellationToken,
                            timeout
                        );
                    }
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));

                    int outputBytes = checked((int)(preflight.Width * preflight.Height * 4UL));
                    if (
                        !TryAllocate(
                            instance,
                            outputBytes,
                            out outputPointer,
                            out allocationStatus
                        )
                    )
                    {
                        return ResolveInterruptedStatus(
                            allocationStatus,
                            cancellationToken,
                            timeout
                        );
                    }
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));
                    if (
                        !TryAllocate(
                            instance,
                            sizeof(ulong),
                            out durationPointer,
                            out allocationStatus
                        )
                    )
                    {
                        return ResolveInterruptedStatus(
                            allocationStatus,
                            cancellationToken,
                            timeout
                        );
                    }
                    peakMemory = Math.Max(peakMemory, GetMemorySizeBytes(instance));

                    var openArgs = new[]
                    {
                        WasmtimeNative.WasmtimeValue.I32(inputPointer),
                        WasmtimeNative.WasmtimeValue.I32(canonicalProfile1Container.Length),
                        WasmtimeNative.WasmtimeValue.I32(checked((int)preflight.Width)),
                        WasmtimeNative.WasmtimeValue.I32(checked((int)preflight.Height)),
                    };
                    if (!TryCallI32(instance, "p1_decode_open", openArgs, out int openStatus, out BasisProfile1SandboxStatus callStatus))
                        return ResolveInterruptedStatus(callStatus, cancellationToken, timeout);
                    if (openStatus != 0)
                        return MapDecoderStatus((uint)openStatus);
                    sessionOpen = true;

                    byte[] reusableFrame = new byte[outputBytes];
                    for (int frameIndex = 0; frameIndex < preflight.LogicalFrameCount; frameIndex++)
                    {
                        var nextArgs = new[]
                        {
                            WasmtimeNative.WasmtimeValue.I32(outputPointer),
                            WasmtimeNative.WasmtimeValue.I32(outputBytes),
                            WasmtimeNative.WasmtimeValue.I32(durationPointer),
                        };
                        if (!TryCallI32(instance, "p1_decode_next", nextArgs, out int nextStatus, out callStatus))
                            return ResolveInterruptedStatus(callStatus, cancellationToken, timeout);
                        if (nextStatus != 0)
                            return MapDecoderStatus((uint)nextStatus);

                        if (!TryCopyFromMemory(instance, outputPointer, reusableFrame))
                            return BasisProfile1SandboxStatus.SandboxFailure;
                        if (!TryReadUInt64(instance, durationPointer, out ulong duration))
                            return BasisProfile1SandboxStatus.SandboxFailure;
                        if (
                            frameIndex >= preflight.FrameDurationsMicroseconds.Length
                            || duration != preflight.FrameDurationsMicroseconds[frameIndex]
                        )
                        {
                            return BasisProfile1SandboxStatus.Malformed;
                        }

                        if (!consumer(frameIndex, reusableFrame, duration))
                            return BasisProfile1SandboxStatus.Cancelled;
                    }

                    if (!TryCallI32(instance, "p1_decode_next", new[]
                        {
                            WasmtimeNative.WasmtimeValue.I32(outputPointer),
                            WasmtimeNative.WasmtimeValue.I32(outputBytes),
                            WasmtimeNative.WasmtimeValue.I32(durationPointer),
                        }, out int endStatus, out BasisProfile1SandboxStatus endCallStatus))
                    {
                        return ResolveInterruptedStatus(endCallStatus, cancellationToken, timeout);
                    }
                    return (uint)endStatus == DecodeEndOfStream
                        ? BasisProfile1SandboxStatus.Success
                        : BasisProfile1SandboxStatus.Malformed;
                }
                finally
                {
                    fuelConsumedAvailable = TryGetFuelConsumed(instance, out fuelConsumed);
                    ulong finalMemory = GetMemorySizeBytes(instance);
                    peakMemory = Math.Max(peakMemory, finalMemory);
                    executionMetrics = new BasisProfile1SandboxExecutionMetrics(
                        initialMemory,
                        peakMemory,
                        finalMemory,
                        instance.MemoryGrowthCount
                    );
                    if (sessionOpen)
                        CallVoidBestEffort(instance, "p1_decode_close");
                    if (durationPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(durationPointer));
                    if (outputPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(outputPointer));
                    if (inputPointer != 0)
                        CallVoidBestEffort(instance, "p1_free", WasmtimeNative.WasmtimeValue.I32(inputPointer));
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                if (_module != IntPtr.Zero)
                {
                    WasmtimeNative.wasmtime_module_delete(_module);
                    _module = IntPtr.Zero;
                }
                if (_engine != IntPtr.Zero)
                {
                    WasmtimeNative.wasm_engine_delete(_engine);
                    _engine = IntPtr.Zero;
                }
            }
        }

        private void InitializeEngineAndModule(byte[] wasmModule)
        {
            IntPtr config = WasmtimeNative.wasm_config_new();
            if (config == IntPtr.Zero)
                throw new InvalidOperationException("Wasmtime configuration allocation failed.");

            WasmtimeNative.wasmtime_config_consume_fuel_set(config, true);
            WasmtimeNative.wasmtime_config_epoch_interruption_set(config, true);
#if UNITY_ANDROID && !UNITY_EDITOR
            IntPtr targetError = WasmtimeNative.wasmtime_config_target_set(config, "pulley64");
            if (targetError != IntPtr.Zero)
            {
                WasmtimeNative.wasmtime_error_delete(targetError);
                throw new InvalidOperationException("Wasmtime Pulley target configuration failed.");
            }
#endif
            _engine = WasmtimeNative.wasm_engine_new_with_config(config);
            if (_engine == IntPtr.Zero)
                throw new InvalidOperationException("Wasmtime engine creation failed.");

            IntPtr error = WasmtimeNative.wasmtime_module_new(
                _engine,
                wasmModule,
                (UIntPtr)(uint)wasmModule.Length,
                out _module
            );
            if (error != IntPtr.Zero || _module == IntPtr.Zero)
            {
                if (error != IntPtr.Zero)
                    WasmtimeNative.wasmtime_error_delete(error);
                throw new InvalidOperationException("Profile 1 WASM module compilation failed.");
            }
        }

        private void ValidateImportContract()
        {
            WasmtimeNative.wasmtime_module_imports(_module, out WasmtimeNative.WasmImportTypeVec imports);
            try
            {
                if (imports.Size.ToUInt64() != 1 || imports.Data == IntPtr.Zero)
                    throw new InvalidOperationException("Profile 1 WASM must have exactly one host import.");

                IntPtr importType = Marshal.ReadIntPtr(imports.Data);
                string module = ReadWasmName(WasmtimeNative.wasm_importtype_module(importType));
                string name = ReadWasmName(WasmtimeNative.wasm_importtype_name(importType));
                if (module != "env" || name != "emscripten_notify_memory_growth")
                {
                    throw new InvalidOperationException(
                        $"Unexpected Profile 1 WASM import '{module}.{name}'."
                    );
                }
                if (
                    WasmtimeNative.wasm_externtype_as_functype_const(
                        WasmtimeNative.wasm_importtype_type(importType)
                    ) == IntPtr.Zero
                )
                {
                    throw new InvalidOperationException("Profile 1 WASM memory-growth import is not a function.");
                }
            }
            finally
            {
                WasmtimeNative.wasm_importtype_vec_delete(ref imports);
            }
        }

        private SandboxInstance CreateInstance()
        {
            IntPtr store = WasmtimeNative.wasmtime_store_new(_engine, IntPtr.Zero, IntPtr.Zero);
            if (store == IntPtr.Zero)
                return SandboxInstance.Failed(BasisProfile1SandboxStatus.SandboxFailure);

            var instance = new SandboxInstance(store);
            IntPtr context = instance.Context;
            WasmtimeNative.wasmtime_store_limiter(
                store,
                _limits.MaximumLinearMemoryBytes,
                -1,
                1,
                1,
                1
            );
            IntPtr fuelError = WasmtimeNative.wasmtime_context_set_fuel(context, _limits.Fuel);
            if (fuelError != IntPtr.Zero)
            {
                WasmtimeNative.wasmtime_error_delete(fuelError);
                instance.Dispose();
                return SandboxInstance.Failed(BasisProfile1SandboxStatus.SandboxFailure);
            }
            WasmtimeNative.wasmtime_context_set_epoch_deadline(context, 1);

            WasmtimeNative.wasmtime_module_imports(_module, out WasmtimeNative.WasmImportTypeVec imports);
            try
            {
                if (imports.Size.ToUInt64() != 1 || imports.Data == IntPtr.Zero)
                {
                    instance.Dispose();
                    return SandboxInstance.Failed(BasisProfile1SandboxStatus.SandboxFailure);
                }
                IntPtr importType = Marshal.ReadIntPtr(imports.Data);
                IntPtr functionType = WasmtimeNative.wasm_externtype_as_functype_const(
                    WasmtimeNative.wasm_importtype_type(importType)
                );
                WasmtimeNative.wasmtime_func_new(
                    context,
                    functionType,
                    MemoryGrowthCallback,
                    instance.MemoryGrowthEnvironment,
                    IntPtr.Zero,
                    out WasmtimeNative.WasmtimeFunc memoryGrowthFunction
                );

                var externs = new[]
                {
                    new WasmtimeNative.WasmtimeExtern
                    {
                        Kind = WasmtimeNative.ExternFunction,
                        Function = memoryGrowthFunction,
                    },
                };
                IntPtr instantiateError = WasmtimeNative.wasmtime_instance_new(
                    context,
                    _module,
                    externs,
                    (UIntPtr)1,
                    out instance.Instance,
                    out IntPtr trap
                );
                if (instantiateError != IntPtr.Zero || trap != IntPtr.Zero)
                {
                    if (instantiateError != IntPtr.Zero)
                        WasmtimeNative.wasmtime_error_delete(instantiateError);
                    if (trap != IntPtr.Zero)
                        WasmtimeNative.wasm_trap_delete(trap);
                    instance.Dispose();
                    return SandboxInstance.Failed(BasisProfile1SandboxStatus.SandboxFailure);
                }
            }
            finally
            {
                WasmtimeNative.wasm_importtype_vec_delete(ref imports);
            }

            if (!TryGetMemory(instance, "memory", out instance.Memory))
            {
                instance.Dispose();
                return SandboxInstance.Failed(BasisProfile1SandboxStatus.SandboxFailure);
            }
            return instance;
        }

        private static bool TryGetMemory(
            SandboxInstance instance,
            string name,
            out WasmtimeNative.WasmtimeMemory memory
        )
        {
            memory = default;
            if (!WasmtimeNative.wasmtime_instance_export_get(
                    instance.Context,
                    ref instance.Instance,
                    name,
                    (UIntPtr)(uint)name.Length,
                    out WasmtimeNative.WasmtimeExtern item))
            {
                return false;
            }
            try
            {
                if (item.Kind != WasmtimeNative.ExternMemory)
                    return false;
                memory = item.Memory;
                return true;
            }
            finally
            {
                WasmtimeNative.wasmtime_extern_delete(ref item);
            }
        }

        private static bool TryGetFunction(
            SandboxInstance instance,
            string name,
            out WasmtimeNative.WasmtimeFunc function
        )
        {
            function = default;
            if (!WasmtimeNative.wasmtime_instance_export_get(
                    instance.Context,
                    ref instance.Instance,
                    name,
                    (UIntPtr)(uint)name.Length,
                    out WasmtimeNative.WasmtimeExtern item))
            {
                return false;
            }
            try
            {
                if (item.Kind != WasmtimeNative.ExternFunction)
                    return false;
                function = item.Function;
                return true;
            }
            finally
            {
                WasmtimeNative.wasmtime_extern_delete(ref item);
            }
        }

        private static bool TryCallI32(
            SandboxInstance instance,
            string functionName,
            WasmtimeNative.WasmtimeValue[] arguments,
            out int result,
            out BasisProfile1SandboxStatus failureStatus
        )
        {
            result = 0;
            failureStatus = BasisProfile1SandboxStatus.SandboxFailure;
            if (!TryGetFunction(instance, functionName, out WasmtimeNative.WasmtimeFunc function))
                return false;

            var results = new[] { WasmtimeNative.WasmtimeValue.I32(0) };
            IntPtr error = WasmtimeNative.wasmtime_func_call(
                instance.Context,
                ref function,
                arguments,
                (UIntPtr)(uint)(arguments?.Length ?? 0),
                results,
                (UIntPtr)1,
                out IntPtr trap
            );
            if (error != IntPtr.Zero)
            {
                WasmtimeNative.wasmtime_error_delete(error);
                return false;
            }
            if (trap != IntPtr.Zero)
            {
                failureStatus = ClassifyTrap(trap);
                WasmtimeNative.wasm_trap_delete(trap);
                return false;
            }
            if (results[0].Kind != WasmtimeNative.ValueI32)
                return false;
            result = unchecked((int)results[0].I64);
            failureStatus = BasisProfile1SandboxStatus.Success;
            return true;
        }

        private static bool TryCallVoid(
            SandboxInstance instance,
            string functionName,
            WasmtimeNative.WasmtimeValue[] arguments,
            out BasisProfile1SandboxStatus failureStatus
        )
        {
            failureStatus = BasisProfile1SandboxStatus.SandboxFailure;
            if (!TryGetFunction(instance, functionName, out WasmtimeNative.WasmtimeFunc function))
                return false;

            IntPtr error = WasmtimeNative.wasmtime_func_call(
                instance.Context,
                ref function,
                arguments,
                (UIntPtr)(uint)(arguments?.Length ?? 0),
                null,
                UIntPtr.Zero,
                out IntPtr trap
            );
            if (error != IntPtr.Zero)
            {
                WasmtimeNative.wasmtime_error_delete(error);
                return false;
            }
            if (trap != IntPtr.Zero)
            {
                failureStatus = ClassifyTrap(trap);
                WasmtimeNative.wasm_trap_delete(trap);
                return false;
            }
            failureStatus = BasisProfile1SandboxStatus.Success;
            return true;
        }

        private static void CallVoidBestEffort(
            SandboxInstance instance,
            string functionName,
            params WasmtimeNative.WasmtimeValue[] arguments
        ) => TryCallVoid(instance, functionName, arguments, out _);

        private static bool TryAllocate(
            SandboxInstance instance,
            int size,
            out int pointer,
            out BasisProfile1SandboxStatus failureStatus
        )
        {
            pointer = 0;
            failureStatus = BasisProfile1SandboxStatus.SandboxFailure;
            if (size <= 0)
                return false;
            if (!TryCallI32(
                    instance,
                    "p1_alloc",
                    new[] { WasmtimeNative.WasmtimeValue.I32(size) },
                    out pointer,
                    out failureStatus))
            {
                return false;
            }
            if (pointer == 0)
            {
                failureStatus = BasisProfile1SandboxStatus.SandboxFailure;
                return false;
            }
            failureStatus = BasisProfile1SandboxStatus.Success;
            return true;
        }

        private static bool TryAllocateAndCopy(
            SandboxInstance instance,
            byte[] bytes,
            out int pointer,
            out BasisProfile1SandboxStatus failureStatus
        )
        {
            pointer = 0;
            failureStatus = BasisProfile1SandboxStatus.SandboxFailure;
            if (!TryAllocate(instance, bytes.Length, out pointer, out failureStatus))
                return false;
            if (!TryGetMemorySpan(instance, pointer, bytes.Length, out IntPtr destination))
            {
                failureStatus = BasisProfile1SandboxStatus.SandboxFailure;
                return false;
            }
            Marshal.Copy(bytes, 0, destination, bytes.Length);
            failureStatus = BasisProfile1SandboxStatus.Success;
            return true;
        }

        private static bool TryCopyFromMemory(
            SandboxInstance instance,
            int pointer,
            byte[] destination
        )
        {
            if (!TryGetMemorySpan(instance, pointer, destination.Length, out IntPtr source))
                return false;
            Marshal.Copy(source, destination, 0, destination.Length);
            return true;
        }

        private static bool TryReadUInt64(
            SandboxInstance instance,
            int pointer,
            out ulong value
        )
        {
            value = 0;
            if (!TryGetMemorySpan(instance, pointer, sizeof(long), out IntPtr source))
                return false;
            value = unchecked((ulong)Marshal.ReadInt64(source));
            return true;
        }

        private static bool TryReadResult(
            SandboxInstance instance,
            int resultPointer,
            out BasisProfile1SandboxPreflight result
        )
        {
            result = default;
            int byteCount = checked(ExpectedResultSlots * sizeof(ulong));
            if (!TryGetMemorySpan(instance, resultPointer, byteCount, out IntPtr memory))
                return false;

            ulong abi = ReadSlot(memory, 0);
            ulong statusRaw = ReadSlot(memory, 1);
            if (abi != DecoderAbiVersion || statusRaw > uint.MaxValue)
                return false;

            BasisProfile1SandboxStatus status = MapDecoderStatus((uint)statusRaw);
            BasisProfile1SandboxDiagnosticReason diagnosticReason = MapDiagnosticReason(ReadSlot(memory, DiagnosticReasonSlot));
            if (status != BasisProfile1SandboxStatus.Success)
            {
                result = new BasisProfile1SandboxPreflight(status, diagnosticReason);
                return true;
            }

            ulong durationCount = ReadSlot(memory, 16);
            if (durationCount == 0 || durationCount > MaximumFrames)
                return false;
            var durations = new ulong[checked((int)durationCount)];
            for (int i = 0; i < durations.Length; i++)
                durations[i] = ReadSlot(memory, ResultHeaderSlots + i);

            ulong logicalFrameCount = ReadSlot(memory, 4);
            if (logicalFrameCount != durationCount || logicalFrameCount > uint.MaxValue)
                return false;

            result = new BasisProfile1SandboxPreflight(
                BasisProfile1SandboxStatus.Success,
                BasisProfile1SandboxDiagnosticReason.None,
                checked((uint)ReadSlot(memory, 2)),
                checked((uint)ReadSlot(memory, 3)),
                checked((uint)logicalFrameCount),
                checked((uint)ReadSlot(memory, 5)),
                ReadSlot(memory, 6),
                ReadSlot(memory, 7),
                ReadSlot(memory, 8),
                ReadSlot(memory, 9),
                ReadSlot(memory, 10),
                ReadSlot(memory, 11),
                ReadSlot(memory, 12),
                ReadSlot(memory, 13),
                ReadSlot(memory, 14),
                ReadSlot(memory, 15),
                ReadSlot(memory, CroppedLayerPixelsSlot),
                ReadSlot(memory, ReferenceReadPixelsSlot),
                ReadSlot(memory, SavedReferencePixelsSlot),
                ReadSlot(memory, BlendOperationPixelsSlot),
                ReadSlot(memory, ReferenceChainExtraPixelsSlot),
                ReadSlot(memory, DecodeWorkCandidateSlot),
                durations
            );
            return true;
        }

        private static ulong ReadSlot(IntPtr memory, int slot) =>
            unchecked((ulong)Marshal.ReadInt64(memory, checked(slot * sizeof(long))));

        private static bool TryGetMemorySpan(
            SandboxInstance instance,
            int offset,
            int length,
            out IntPtr address
        )
        {
            address = IntPtr.Zero;
            if (offset < 0 || length < 0)
                return false;
            ulong end = (ulong)(uint)offset + (ulong)(uint)length;
            ulong size = WasmtimeNative.wasmtime_memory_data_size(
                instance.Context,
                ref instance.Memory
            ).ToUInt64();
            if (end > size)
                return false;
            IntPtr baseAddress = WasmtimeNative.wasmtime_memory_data(
                instance.Context,
                ref instance.Memory
            );
            if (baseAddress == IntPtr.Zero)
                return false;
            address = IntPtr.Add(baseAddress, offset);
            return true;
        }

        private static ulong GetMemorySizeBytes(SandboxInstance instance)
        {
            if (instance == null || !instance.IsValid)
                return 0;
            return WasmtimeNative.wasmtime_memory_data_size(
                instance.Context,
                ref instance.Memory
            ).ToUInt64();
        }

        private static bool TryGetFuelRemaining(SandboxInstance instance, out ulong remaining)
        {
            remaining = 0;
            if (instance == null || !instance.IsValid)
                return false;
            IntPtr error = WasmtimeNative.wasmtime_context_get_fuel(instance.Context, out remaining);
            if (error == IntPtr.Zero)
                return true;
            WasmtimeNative.wasmtime_error_delete(error);
            remaining = 0;
            return false;
        }

        private bool TryGetFuelConsumed(SandboxInstance instance, out ulong fuelConsumed)
        {
            fuelConsumed = 0;
            if (!TryGetFuelRemaining(instance, out ulong remaining))
                return false;
            fuelConsumed = remaining <= _limits.Fuel ? _limits.Fuel - remaining : 0;
            return true;
        }

        private TimeoutScope CreateTimeout(CancellationToken cancellationToken) =>
            new TimeoutScope(cancellationToken, _limits.Timeout);

        private static BasisProfile1SandboxStatus ResolveCancellationStatus(
            CancellationToken callerToken
        ) => callerToken.IsCancellationRequested
            ? BasisProfile1SandboxStatus.Cancelled
            : BasisProfile1SandboxStatus.Timeout;

        private static BasisProfile1SandboxStatus ResolveInterruptedStatus(
            BasisProfile1SandboxStatus callStatus,
            CancellationToken callerToken,
            TimeoutScope timeout
        )
        {
            if (callStatus != BasisProfile1SandboxStatus.Timeout)
                return callStatus;
            if (callerToken.IsCancellationRequested)
                return BasisProfile1SandboxStatus.Cancelled;
            return timeout.TimedOut
                ? BasisProfile1SandboxStatus.Timeout
                : callStatus;
        }

        private static BasisProfile1SandboxStatus ClassifyTrap(IntPtr trap)
        {
            if (WasmtimeNative.wasmtime_trap_code(trap, out byte code))
            {
                if (code == WasmtimeNative.TrapOutOfFuel)
                    return BasisProfile1SandboxStatus.OutOfFuel;
                if (code == WasmtimeNative.TrapInterrupt)
                    return BasisProfile1SandboxStatus.Timeout;
            }
            return BasisProfile1SandboxStatus.SandboxFailure;
        }

        private static BasisProfile1SandboxStatus MapDecoderStatus(uint status) =>
            status switch
            {
                0 => BasisProfile1SandboxStatus.Success,
                1 => BasisProfile1SandboxStatus.Malformed,
                2 => BasisProfile1SandboxStatus.UnsupportedProfile,
                3 => BasisProfile1SandboxStatus.SharedLimitExceeded,
                _ => BasisProfile1SandboxStatus.SandboxFailure,
            };

        private static BasisProfile1SandboxDiagnosticReason MapDiagnosticReason(ulong reason) =>
            reason <= (ulong)BasisProfile1SandboxDiagnosticReason.DecodeWorkOverflow
                ? (BasisProfile1SandboxDiagnosticReason)reason
                : BasisProfile1SandboxDiagnosticReason.None;

        private static string ReadWasmName(IntPtr namePointer)
        {
            if (namePointer == IntPtr.Zero)
                return string.Empty;
            WasmtimeNative.WasmByteVec name = Marshal.PtrToStructure<WasmtimeNative.WasmByteVec>(namePointer);
            int size = checked((int)name.Size.ToUInt64());
            if (size == 0 || name.Data == IntPtr.Zero)
                return string.Empty;
            byte[] bytes = new byte[size];
            Marshal.Copy(name.Data, bytes, 0, size);
            return Encoding.UTF8.GetString(bytes);
        }

        private static IntPtr IgnoreMemoryGrowthNotification(
            IntPtr environment,
            IntPtr caller,
            IntPtr arguments,
            UIntPtr argumentCount,
            IntPtr results,
            UIntPtr resultCount
        )
        {
            if (environment != IntPtr.Zero)
            {
                try
                {
                    GCHandle handle = GCHandle.FromIntPtr(environment);
                    if (handle.Target is MemoryGrowthTracker tracker)
                        Interlocked.Increment(ref tracker.Count);
                }
                catch (InvalidOperationException)
                {
                }
            }
            return IntPtr.Zero;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(BasisProfile1SandboxDecoder));
        }

        private sealed class MemoryGrowthTracker
        {
            public int Count;
        }

        private sealed class SandboxInstance : IDisposable
        {
            public IntPtr Store;
            public IntPtr Context;
            public WasmtimeNative.WasmtimeInstance Instance;
            public WasmtimeNative.WasmtimeMemory Memory;
            public BasisProfile1SandboxStatus FailureStatus;
            public bool IsValid => Store != IntPtr.Zero;
            public IntPtr MemoryGrowthEnvironment => _memoryGrowthHandle.IsAllocated
                ? GCHandle.ToIntPtr(_memoryGrowthHandle)
                : IntPtr.Zero;
            public int MemoryGrowthCount => _memoryGrowthTracker?.Count ?? 0;

            private MemoryGrowthTracker _memoryGrowthTracker;
            private GCHandle _memoryGrowthHandle;

            public SandboxInstance(IntPtr store)
            {
                Store = store;
                Context = WasmtimeNative.wasmtime_store_context(store);
                FailureStatus = BasisProfile1SandboxStatus.Success;
                _memoryGrowthTracker = new MemoryGrowthTracker();
                _memoryGrowthHandle = GCHandle.Alloc(_memoryGrowthTracker);
            }

            private SandboxInstance(BasisProfile1SandboxStatus failureStatus)
            {
                FailureStatus = failureStatus;
            }

            public static SandboxInstance Failed(BasisProfile1SandboxStatus status) =>
                new SandboxInstance(status);

            public void Dispose()
            {
                if (Store != IntPtr.Zero)
                {
                    WasmtimeNative.wasmtime_store_delete(Store);
                    Store = IntPtr.Zero;
                    Context = IntPtr.Zero;
                }
                if (_memoryGrowthHandle.IsAllocated)
                    _memoryGrowthHandle.Free();
                _memoryGrowthTracker = null;
            }
        }

        private sealed class TimeoutScope : IDisposable
        {
            private readonly CancellationTokenSource _timeout;
            private readonly CancellationTokenSource _linked;
            public CancellationToken Token => _linked.Token;
            public bool TimedOut => _timeout.IsCancellationRequested;

            public TimeoutScope(CancellationToken callerToken, TimeSpan timeout)
            {
                _timeout = new CancellationTokenSource();
                _timeout.CancelAfter(timeout);
                _linked = CancellationTokenSource.CreateLinkedTokenSource(
                    callerToken,
                    _timeout.Token
                );
            }

            public void Dispose()
            {
                _linked.Dispose();
                _timeout.Dispose();
            }
        }
    }
}
