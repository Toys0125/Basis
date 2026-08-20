using System;
using System.IO;
using System.Threading;
using Basis.ImageSandbox;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: smoke <profile1_decoder.wasm> <positive-profile1.jxl>");
    return 2;
}

byte[] wasm = File.ReadAllBytes(args[0]);
byte[] positivePayload = File.ReadAllBytes(args[1]);
var limits = new BasisProfile1SandboxLimits(
    256L * 1024L * 1024L,
    2_000_000_000UL,
    TimeSpan.FromSeconds(5)
);

using var decoder = new BasisProfile1SandboxDecoder(wasm, limits);

BasisProfile1SandboxPreflight malformed = decoder.Preflight(new byte[] { 0 });
if (malformed.Status != BasisProfile1SandboxStatus.Malformed)
{
    Console.Error.WriteLine($"Expected Malformed, got {malformed.Status}.");
    return 3;
}

BasisProfile1SandboxPreflight positive = decoder.Preflight(positivePayload);
if (positive.Status != BasisProfile1SandboxStatus.Success)
{
    Console.Error.WriteLine($"Expected valid fixture success, got {positive.Status}.");
    return 4;
}
if (
    positive.Width != 2
    || positive.Height != 1
    || positive.LogicalFrameCount != 2
    || positive.TotalPlayCount != 0
    || positive.SubmittedCanvasPixels != 4
    || positive.BaseTimelineMicroseconds != 83_335
    || positive.FrameDurationsMicroseconds.Length != 2
    || positive.FrameDurationsMicroseconds[0] != 33_334
    || positive.FrameDurationsMicroseconds[1] != 50_001
)
{
    Console.Error.WriteLine("Valid fixture preflight envelope did not match expected semantics.");
    return 5;
}

byte[][] expectedFrames =
{
    new byte[] { 17, 39, 201, 0, 1, 2, 3, 255 },
    new byte[] { 4, 5, 6, 128, 7, 8, 9, 255 },
};
int seenFrames = 0;
BasisProfile1SandboxStatus decodeStatus = decoder.DecodeFrames(
    positivePayload,
    positive,
    (frameIndex, rgba, duration) =>
    {
        if (!rgba.AsSpan().SequenceEqual(expectedFrames[frameIndex]))
            return false;
        seenFrames++;
        return true;
    }
);
if (decodeStatus != BasisProfile1SandboxStatus.Success || seenFrames != 2)
{
    Console.Error.WriteLine(
        $"Expected exact two-frame decode, got status {decodeStatus} and {seenFrames} frames."
    );
    return 6;
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
    if (exhausted.Status != BasisProfile1SandboxStatus.Timeout)
    {
        Console.Error.WriteLine($"Expected fuel exhaustion Timeout, got {exhausted.Status}.");
        return 7;
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
    return 8;
}

Console.WriteLine("Profile 1 Wasmtime host smoke passed.");
return 0;
