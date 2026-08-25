using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Basis.ImageSandbox;

if (args.Length < 3 || args.Length > 4)
{
    Console.Error.WriteLine("Usage: smoke <profile1_decoder.wasm> <profile1_native_oracle> <positive-profile1.jxl> [profile1_fixture_encoder]");
    return 2;
}

string wasmPath = args[0];
string nativeOraclePath = args[1];
string positivePath = args[2];
string fixtureEncoderPath = args.Length == 4 ? args[3] : null;
byte[] wasm = File.ReadAllBytes(wasmPath);
byte[] positivePayload = File.ReadAllBytes(positivePath);
var limits = new BasisProfile1SandboxLimits(
    256L * 1024L * 1024L,
    2_000_000_000UL,
    TimeSpan.FromSeconds(5)
);

using var decoder = new BasisProfile1SandboxDecoder(wasm, limits);

string malformedPath = Path.GetTempFileName();
string semanticTruncatedPath = Path.GetTempFileName();
try
{
    File.WriteAllBytes(malformedPath, new byte[] { 0 });
    File.WriteAllBytes(
        semanticTruncatedPath,
        Convert.FromHexString(
            "0000000c4a584c200d0a870a00000014667479706a786c20000000006a786c20"
            + "0000003c6a786c7080000000ff0a0070c17f841e008035010800b08d200000000068004b12a5428524d6f0b802001c00bf800800960e93120d16dd07"
        )
    );

    BasisProfile1SandboxPreflight malformed = decoder.Preflight(new byte[] { 0 });
    NativeOracleResult nativeMalformed = RunNativeOracle(nativeOraclePath, malformedPath);
    if (malformed.Status != BasisProfile1SandboxStatus.Malformed || nativeMalformed.Status != 1)
    {
        Console.Error.WriteLine(
            $"Malformed differential mismatch: WASM={malformed.Status}, native={nativeMalformed.Status}."
        );
        return 3;
    }

    byte[] semanticTruncated = File.ReadAllBytes(semanticTruncatedPath);
    BasisProfile1SandboxPreflight wasmTruncated = decoder.Preflight(semanticTruncated);
    NativeOracleResult nativeTruncated = RunNativeOracle(nativeOraclePath, semanticTruncatedPath);
    if (wasmTruncated.Status != BasisProfile1SandboxStatus.Malformed || nativeTruncated.Status != 1)
    {
        Console.Error.WriteLine(
            $"Semantic-truncation differential mismatch: WASM={wasmTruncated.Status}, native={nativeTruncated.Status}."
        );
        return 4;
    }

    BasisProfile1SandboxPreflight positive = decoder.Preflight(positivePayload);
    NativeOracleResult nativePositive = RunNativeOracle(nativeOraclePath, positivePath);
    if (positive.Status != BasisProfile1SandboxStatus.Success || nativePositive.Status != 0)
    {
        Console.Error.WriteLine(
            $"Expected valid fixture success, got WASM={positive.Status}, native={nativePositive.Status}."
        );
        return 5;
    }
    if (
        positive.Width != 2
        || positive.Height != 1
        || positive.LogicalFrameCount != 2
        || positive.TotalPlayCount != 0
        || positive.SubmittedCanvasPixels != 4
        || positive.BaseTimelineMicroseconds != 83_335
        || positive.PublicRegularLayerCount != 2
        || positive.PublicRegularLayerPixels != 4
        || positive.CroppedLayerCount != 0
        || positive.ReferenceReadEdges != 0
        || positive.SavedReferenceCount != 0
        || positive.BlendOperationCount != 0
        || positive.MaximumReferenceChainDepth != 1
        || positive.PreviewPixels != 0
        || positive.CroppedLayerPixels != 0
        || positive.ReferenceReadPixels != 0
        || positive.SavedReferencePixels != 0
        || positive.BlendOperationPixels != 0
        || positive.ReferenceChainExtraPixels != 0
        || positive.DecodeWorkCandidate != 4_140
        || positive.FrameDurationsMicroseconds.Length != 2
        || positive.FrameDurationsMicroseconds[0] != 33_334
        || positive.FrameDurationsMicroseconds[1] != 50_001
    )
    {
        Console.Error.WriteLine("Valid fixture preflight envelope did not match expected semantics.");
        return 6;
    }
    if (!NativeEnvelopeMatchesWasm(nativePositive, positive, out string differentialError))
    {
        Console.Error.WriteLine("Native/WASM preflight differential failed: " + differentialError);
        return 7;
    }

    byte[][] expectedFrames =
    {
        new byte[] { 17, 39, 201, 0, 1, 2, 3, 255 },
        new byte[] { 4, 5, 6, 128, 7, 8, 9, 255 },
    };
    if (nativePositive.Frames.Count != expectedFrames.Length)
    {
        Console.Error.WriteLine($"Native oracle decoded {nativePositive.Frames.Count} frames instead of 2.");
        return 8;
    }
    for (int i = 0; i < expectedFrames.Length; i++)
    {
        if (!nativePositive.Frames[i].Bytes.AsSpan().SequenceEqual(expectedFrames[i]) ||
            nativePositive.Frames[i].Duration != positive.FrameDurationsMicroseconds[i])
        {
            Console.Error.WriteLine($"Native oracle frame {i} did not match the expected RGBA/duration.");
            return 9;
        }
    }

    int seenFrames = 0;
    BasisProfile1SandboxStatus decodeStatus = decoder.DecodeFrames(
        positivePayload,
        positive,
        (frameIndex, rgba, duration) =>
        {
            if (frameIndex < 0 || frameIndex >= expectedFrames.Length ||
                !rgba.AsSpan().SequenceEqual(expectedFrames[frameIndex]) ||
                !rgba.AsSpan().SequenceEqual(nativePositive.Frames[frameIndex].Bytes) ||
                duration != nativePositive.Frames[frameIndex].Duration)
            {
                return false;
            }
            seenFrames++;
            return true;
        }
    );
    if (decodeStatus != BasisProfile1SandboxStatus.Success || seenFrames != 2)
    {
        Console.Error.WriteLine(
            $"Expected exact two-frame differential decode, got status {decodeStatus} and {seenFrames} frames."
        );
        return 10;
    }

    if (!string.IsNullOrEmpty(fixtureEncoderPath))
    {
        var negativeCases = new (string Variant, BasisProfile1SandboxDiagnosticReason Reason)[]
        {
            ("bits16", BasisProfile1SandboxDiagnosticReason.BitsPerSample),
            ("grayscale", BasisProfile1SandboxDiagnosticReason.ColorChannels),
            ("no-alpha", BasisProfile1SandboxDiagnosticReason.ExtraChannels),
            ("alpha16", BasisProfile1SandboxDiagnosticReason.Alpha),
            ("premultiplied-alpha", BasisProfile1SandboxDiagnosticReason.PremultipliedAlpha),
            ("orientation", BasisProfile1SandboxDiagnosticReason.Orientation),
            ("no-animation", BasisProfile1SandboxDiagnosticReason.MissingAnimation),
            ("wrong-timebase", BasisProfile1SandboxDiagnosticReason.Timebase),
            ("linear-srgb", BasisProfile1SandboxDiagnosticReason.ColorEncoding),
        };
        foreach ((string variant, BasisProfile1SandboxDiagnosticReason expectedReason) in negativeCases)
        {
            string path = Path.Combine(Path.GetTempPath(), $"basis-profile1-{variant}-{Guid.NewGuid():N}.jxl");
            try
            {
                RunFixtureEncoder(fixtureEncoderPath, path, variant);
                byte[] payload = File.ReadAllBytes(path);
                BasisProfile1SandboxPreflight wasmNegative = decoder.Preflight(payload);
                NativeOracleResult nativeNegative = RunNativeOracle(nativeOraclePath, path);
                const int DiagnosticReasonSlot = 17 + 512;
                ulong nativeReason = nativeNegative.Slots.Length > DiagnosticReasonSlot
                    ? nativeNegative.Slots[DiagnosticReasonSlot]
                    : ulong.MaxValue;
                if (wasmNegative.Status != BasisProfile1SandboxStatus.UnsupportedProfile ||
                    wasmNegative.DiagnosticReason != expectedReason ||
                    nativeNegative.Status != 2 ||
                    nativeReason != (ulong)expectedReason)
                {
                    Console.Error.WriteLine(
                        $"Negative differential {variant} mismatch: WASM={wasmNegative.Status}/{wasmNegative.DiagnosticReason}, "
                        + $"native={nativeNegative.Status}/{nativeReason}, expected UnsupportedProfile/{expectedReason}."
                    );
                    return 11;
                }
            }
            finally
            {
                TryDelete(path);
            }
        }

        var boundaryCases = new (string Variant, BasisProfile1SandboxStatus Status, BasisProfile1SandboxDiagnosticReason Reason, uint Frames, ulong Submitted, ulong Timeline)[]
        {
            ("width-over", BasisProfile1SandboxStatus.SharedLimitExceeded, BasisProfile1SandboxDiagnosticReason.Dimensions, 0, 0, 0),
            ("height-over", BasisProfile1SandboxStatus.SharedLimitExceeded, BasisProfile1SandboxDiagnosticReason.Dimensions, 0, 0, 0),
            ("logical-frames-exact", BasisProfile1SandboxStatus.Success, BasisProfile1SandboxDiagnosticReason.None, 512, 32_768, 17_067_008),
            ("logical-frames-over", BasisProfile1SandboxStatus.SharedLimitExceeded, BasisProfile1SandboxDiagnosticReason.LogicalFrames, 0, 0, 0),
            ("submitted-below", BasisProfile1SandboxStatus.Success, BasisProfile1SandboxDiagnosticReason.None, 510, 33_553_920, 17_000_340),
            ("submitted-exact", BasisProfile1SandboxStatus.Success, BasisProfile1SandboxDiagnosticReason.None, 512, 33_554_432, 17_067_008),
            ("submitted-over", BasisProfile1SandboxStatus.SharedLimitExceeded, BasisProfile1SandboxDiagnosticReason.SubmittedPixels, 0, 0, 0),
            ("timeline-below", BasisProfile1SandboxStatus.Success, BasisProfile1SandboxDiagnosticReason.None, 1, 64, 299_999_999),
            ("timeline-exact", BasisProfile1SandboxStatus.Success, BasisProfile1SandboxDiagnosticReason.None, 1, 64, 300_000_000),
            ("timeline-over", BasisProfile1SandboxStatus.SharedLimitExceeded, BasisProfile1SandboxDiagnosticReason.Timeline, 0, 0, 0),
            ("duration-below", BasisProfile1SandboxStatus.SharedLimitExceeded, BasisProfile1SandboxDiagnosticReason.FrameDuration, 0, 0, 0),
            ("duration-exact", BasisProfile1SandboxStatus.Success, BasisProfile1SandboxDiagnosticReason.None, 1, 64, 33_334),
        };
        foreach (var boundary in boundaryCases)
        {
            string path = Path.Combine(Path.GetTempPath(), $"basis-profile1-{boundary.Variant}-{Guid.NewGuid():N}.jxl");
            try
            {
                RunFixtureEncoder(fixtureEncoderPath, path, boundary.Variant);
                BasisProfile1SandboxPreflight wasmBoundary = decoder.Preflight(File.ReadAllBytes(path));
                NativeOracleResult nativeBoundary = RunNativeOracle(nativeOraclePath, path, preflightOnly: true);
                const int DiagnosticReasonSlot = 17 + 512;
                ulong nativeReason = nativeBoundary.Slots.Length > DiagnosticReasonSlot
                    ? nativeBoundary.Slots[DiagnosticReasonSlot]
                    : ulong.MaxValue;
                uint expectedNativeStatus = boundary.Status == BasisProfile1SandboxStatus.Success ? 0U : 3U;
                if (wasmBoundary.Status != boundary.Status || nativeBoundary.Status != expectedNativeStatus ||
                    (boundary.Status != BasisProfile1SandboxStatus.Success &&
                        (wasmBoundary.DiagnosticReason != boundary.Reason || nativeReason != (ulong)boundary.Reason)))
                {
                    Console.Error.WriteLine(
                        $"Boundary differential {boundary.Variant} mismatch: WASM={wasmBoundary.Status}/{wasmBoundary.DiagnosticReason}, "
                        + $"native={nativeBoundary.Status}/{nativeReason}, expected {boundary.Status}/{boundary.Reason}."
                    );
                    return 12;
                }
                if (boundary.Status == BasisProfile1SandboxStatus.Success)
                {
                    bool nativeMatches = NativeEnvelopeMatchesWasm(
                        nativeBoundary,
                        wasmBoundary,
                        out string boundaryError
                    );
                    if (wasmBoundary.LogicalFrameCount != boundary.Frames ||
                        wasmBoundary.SubmittedCanvasPixels != boundary.Submitted ||
                        wasmBoundary.BaseTimelineMicroseconds != boundary.Timeline ||
                        !nativeMatches)
                    {
                        Console.Error.WriteLine(
                            $"Boundary differential {boundary.Variant} accepted-envelope mismatch: {boundaryError}."
                        );
                        return 13;
                    }
                }
            }
            finally
            {
                TryDelete(path);
            }
        }
    }

    using (var fuelLimitedDecoder = new BasisProfile1SandboxDecoder(
        wasm,
        new BasisProfile1SandboxLimits(
            256L * 1024L * 1024L,
            1UL,
            TimeSpan.FromSeconds(5)
        )
    ))
    {
        BasisProfile1SandboxPreflight exhausted = fuelLimitedDecoder.Preflight(positivePayload);
        if (exhausted.Status != BasisProfile1SandboxStatus.OutOfFuel)
        {
            Console.Error.WriteLine($"Expected fuel exhaustion OutOfFuel, got {exhausted.Status}.");
            return 12;
        }
    }

    using var cancelledSource = new CancellationTokenSource();
    cancelledSource.Cancel();
    BasisProfile1SandboxPreflight cancelled = decoder.Preflight(
        new byte[] { 0 },
        cancelledSource.Token
    );
    if (cancelled.Status != BasisProfile1SandboxStatus.Cancelled)
    {
        Console.Error.WriteLine($"Expected Cancelled, got {cancelled.Status}.");
        return 13;
    }
}
finally
{
    TryDelete(malformedPath);
    TryDelete(semanticTruncatedPath);
}

Console.WriteLine("Profile 1 native/WASM differential smoke passed.");
return 0;

static void RunFixtureEncoder(string executable, string outputPath, string variant)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(outputPath);
    startInfo.ArgumentList.Add(variant);
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start Profile 1 fixture encoder.");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Profile 1 fixture encoder ({variant}) exited {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}"
        );
    }
}

static NativeOracleResult RunNativeOracle(
    string executable,
    string payloadPath,
    bool preflightOnly = false
)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    startInfo.ArgumentList.Add(payloadPath);
    if (preflightOnly)
        startInfo.ArgumentList.Add("--preflight-only");
    using Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Could not start Profile 1 native oracle.");
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Profile 1 native oracle exited {process.ExitCode}: {stderr.Trim()}"
        );
    }

    string[] lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    string resultLine = lines.FirstOrDefault(line => line.StartsWith("RESULT ", StringComparison.Ordinal))
        ?? throw new InvalidOperationException("Native oracle did not emit RESULT.");
    string[] parts = resultLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 4 || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint status) ||
        !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int slotCount) ||
        slotCount <= 0 || parts.Length != slotCount + 3)
    {
        throw new InvalidOperationException("Native oracle RESULT was malformed.");
    }
    var slots = new ulong[slotCount];
    for (int i = 0; i < slotCount; i++)
    {
        if (!ulong.TryParse(parts[i + 3], NumberStyles.None, CultureInfo.InvariantCulture, out slots[i]))
            throw new InvalidOperationException("Native oracle RESULT contained an invalid slot.");
    }

    var frames = new List<NativeFrame>();
    foreach (string line in lines.Where(line => line.StartsWith("FRAME ", StringComparison.Ordinal)))
    {
        string[] frameParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (frameParts.Length != 4 ||
            !int.TryParse(frameParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int frameIndex) ||
            frameIndex != frames.Count ||
            !ulong.TryParse(frameParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out ulong duration))
        {
            throw new InvalidOperationException("Native oracle FRAME was malformed.");
        }
        frames.Add(new NativeFrame(duration, Convert.FromHexString(frameParts[3])));
    }
    return new NativeOracleResult(status, slots, frames);
}

static bool NativeEnvelopeMatchesWasm(
    NativeOracleResult native,
    BasisProfile1SandboxPreflight wasm,
    out string error)
{
    error = null;
    const int ResultHeaderSlots = 17;
    const int MaximumFrames = 512;
    const int DiagnosticReasonSlot = ResultHeaderSlots + MaximumFrames;
    const int CroppedLayerPixelsSlot = DiagnosticReasonSlot + 1;
    const int ReferenceReadPixelsSlot = CroppedLayerPixelsSlot + 1;
    const int SavedReferencePixelsSlot = ReferenceReadPixelsSlot + 1;
    const int BlendOperationPixelsSlot = SavedReferencePixelsSlot + 1;
    const int ReferenceChainExtraPixelsSlot = BlendOperationPixelsSlot + 1;
    const int DecodeWorkCandidateSlot = ReferenceChainExtraPixelsSlot + 1;
    const int ExpectedSlots = DecodeWorkCandidateSlot + 1;

    if (native.Slots.Length != ExpectedSlots || native.Slots[0] != 1 || native.Slots[1] != 0)
    {
        error = $"native ABI/result slots were unexpected ({native.Slots.Length}).";
        return false;
    }

    bool fixedFieldsMatch =
        native.Slots[2] == wasm.Width &&
        native.Slots[3] == wasm.Height &&
        native.Slots[4] == wasm.LogicalFrameCount &&
        native.Slots[5] == wasm.TotalPlayCount &&
        native.Slots[6] == wasm.SubmittedCanvasPixels &&
        native.Slots[7] == wasm.BaseTimelineMicroseconds &&
        native.Slots[8] == wasm.PublicRegularLayerCount &&
        native.Slots[9] == wasm.PublicRegularLayerPixels &&
        native.Slots[10] == wasm.CroppedLayerCount &&
        native.Slots[11] == wasm.ReferenceReadEdges &&
        native.Slots[12] == wasm.SavedReferenceCount &&
        native.Slots[13] == wasm.BlendOperationCount &&
        native.Slots[14] == wasm.MaximumReferenceChainDepth &&
        native.Slots[15] == wasm.PreviewPixels &&
        native.Slots[16] == wasm.LogicalFrameCount &&
        native.Slots[DiagnosticReasonSlot] == 0 &&
        native.Slots[CroppedLayerPixelsSlot] == wasm.CroppedLayerPixels &&
        native.Slots[ReferenceReadPixelsSlot] == wasm.ReferenceReadPixels &&
        native.Slots[SavedReferencePixelsSlot] == wasm.SavedReferencePixels &&
        native.Slots[BlendOperationPixelsSlot] == wasm.BlendOperationPixels &&
        native.Slots[ReferenceChainExtraPixelsSlot] == wasm.ReferenceChainExtraPixels &&
        native.Slots[DecodeWorkCandidateSlot] == wasm.DecodeWorkCandidate;
    if (!fixedFieldsMatch)
    {
        error = "bounded resource-envelope slots differ.";
        return false;
    }
    if (wasm.FrameDurationsMicroseconds.Length != wasm.LogicalFrameCount)
    {
        error = "WASM duration vector length differs from logical frame count.";
        return false;
    }
    for (int i = 0; i < wasm.FrameDurationsMicroseconds.Length; i++)
    {
        if (native.Slots[ResultHeaderSlots + i] != wasm.FrameDurationsMicroseconds[i])
        {
            error = $"duration {i} differs.";
            return false;
        }
    }
    return true;
}

static void TryDelete(string path)
{
    try
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
    }
    catch
    {
        // Temp cleanup is non-gate-bearing.
    }
}

sealed class NativeOracleResult
{
    public uint Status { get; }
    public ulong[] Slots { get; }
    public List<NativeFrame> Frames { get; }

    public NativeOracleResult(uint status, ulong[] slots, List<NativeFrame> frames)
    {
        Status = status;
        Slots = slots;
        Frames = frames;
    }
}

readonly struct NativeFrame
{
    public ulong Duration { get; }
    public byte[] Bytes { get; }

    public NativeFrame(ulong duration, byte[] bytes)
    {
        Duration = duration;
        Bytes = bytes;
    }
}
