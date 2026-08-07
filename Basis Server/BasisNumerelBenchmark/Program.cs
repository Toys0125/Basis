using System.Diagnostics;
using System.Text.Json;
using Basis.Network.Core.Compression;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;

namespace BasisNumerelBenchmark;

internal enum MotionProfile
{
    Static,
    Idle,
    Active,
    Burst
}

internal sealed record CodecSpec(string Name, BasisNumerelArmatureCodec.Options Value);
internal sealed record Quaternion4Spec(string Name, BasisNumerelQuaternion4ArmatureCodec.Options Value);
internal sealed record V3Spec(string Name, BasisAvatarDeltaRecoveryV3.Options Value, bool RecoveryRequests);

internal sealed class LegacyResult
{
    public string Quality { get; set; } = "";
    public string Motion { get; set; } = "";
    public double AverageBodyBytes { get; set; }
    public double AverageFramedBytes { get; set; }
    public int P95BodyBytes { get; set; }
    public int MaxBodyBytes { get; set; }
    public int Keyframes { get; set; }
    public int Deltas { get; set; }
    public double BytesPerSecond20Hz { get; set; }
}

internal sealed class NumerelResult
{
    public string Tuning { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Motion { get; set; } = "";
    public double LossPercent { get; set; }
    public double ReorderPercent { get; set; }
    public double AverageBodyBytes { get; set; }
    public double AverageFramedBytes { get; set; }
    public int P50BodyBytes { get; set; }
    public int P95BodyBytes { get; set; }
    public int P99BodyBytes { get; set; }
    public int MaxBodyBytes { get; set; }
    public double BytesPerSecond20Hz { get; set; }
    public int OfferedFrames { get; set; }
    public int DeliveredDatagrams { get; set; }
    public int SteadyAcceptedFrames { get; set; }
    public int LateAcceptedFrames { get; set; }
    public double SteadyMeanAngularErrorDeg { get; set; }
    public double SteadyP95AngularErrorDeg { get; set; }
    public double SteadyP99AngularErrorDeg { get; set; }
    public double SteadyMaxAngularErrorDeg { get; set; }
    public double LateMeanAngularErrorDeg { get; set; }
    public double LateP95AngularErrorDeg { get; set; }
    public double LateP99AngularErrorDeg { get; set; }
    public double LateMaxAngularErrorDeg { get; set; }
    public double? LateJoinStableUnder1DegMs { get; set; }
    public double? LateJoinStableUnder025DegMs { get; set; }
}

internal sealed class CpuResult
{
    public string Tuning { get; set; } = "";
    public double EncodeNanosecondsPerFrame { get; set; }
    public double DecodeNanosecondsPerFrame { get; set; }
    public long EncodeAllocatedBytes { get; set; }
    public long DecodeAllocatedBytes { get; set; }
}

internal sealed class BenchmarkDocument
{
    public string Runtime { get; set; } = "";
    public string Architecture { get; set; } = "";
    public int FramesPerScenario { get; set; }
    public int SendRateHz { get; set; }
    public List<LegacyResult> Legacy { get; set; } = new();
    public List<NumerelResult> Numerel { get; set; } = new();
    public List<NumerelResult> RecoveryV3 { get; set; } = new();
    public List<CpuResult> Cpu { get; set; } = new();
}

internal sealed record Packet(byte[] Bytes, int Length, byte Sequence, int FrameIndex);

internal sealed class ErrorAccumulator
{
    private readonly List<double> _all = new();
    private int _under1Streak;
    private int _under025Streak;
    private readonly int _joinFrame;

    public ErrorAccumulator(int joinFrame) => _joinFrame = joinFrame;

    public int AcceptedFrames { get; private set; }
    public double? StableUnder1Ms { get; private set; }
    public double? StableUnder025Ms { get; private set; }

    public void AddFrame(byte[] source, byte[] decoded, BitQuality quality, int frameIndex)
    {
        double[] perBone = new double[BasisBoneRotationCompression.SyncBoneCount];
        for (int bone = 0; bone < perBone.Length; bone++)
        {
            double error = AngularError(source, decoded, quality, bone);
            perBone[bone] = error;
            _all.Add(error);
        }
        Array.Sort(perBone);
        double p95 = perBone[(int)Math.Ceiling(perBone.Length * 0.95) - 1];

        AcceptedFrames++;
        _under1Streak = p95 <= 1.0 ? _under1Streak + 1 : 0;
        _under025Streak = p95 <= 0.25 ? _under025Streak + 1 : 0;
        if (StableUnder1Ms == null && _under1Streak >= 5)
            StableUnder1Ms = Math.Max(0, frameIndex - _joinFrame - 4) * 50.0;
        if (StableUnder025Ms == null && _under025Streak >= 5)
            StableUnder025Ms = Math.Max(0, frameIndex - _joinFrame - 4) * 50.0;
    }

    public (double mean, double p95, double p99, double max) Summarize()
    {
        if (_all.Count == 0) return (double.NaN, double.NaN, double.NaN, double.NaN);
        _all.Sort();
        double sum = 0;
        foreach (double value in _all) sum += value;
        return (sum / _all.Count, Percentile(_all, 0.95), Percentile(_all, 0.99), _all[^1]);
    }

    private static double Percentile(List<double> sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

    private static double AngularError(byte[] source, byte[] decoded, BitQuality quality, int bone)
    {
        byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
        int[] offsets = new int[bpc.Length];
        BasisBoneRotationCompression.ComputeBitOffsets(bpc, offsets);
        int width = 2 + 3 * bpc[bone];
        int sourceBit = BasisAvatarBitPacking.PositionBytes(quality) * 8 + offsets[bone];
        int decodedBit = sourceBit;
        ulong a = BasisBoneRotationCompression.ReadBits(source, ref sourceBit, width);
        ulong b = BasisBoneRotationCompression.ReadBits(decoded, ref decodedBit, width);
        BasisBoneRotationCompression.DecodeSmallestThree(a, bpc[bone], out float ax, out float ay, out float az, out float aw, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
        BasisBoneRotationCompression.DecodeSmallestThree(b, bpc[bone], out float bx, out float by, out float bz, out float bw, BasisBoneRotationCompression.MAX_COMPONENT[bone]);
        double dot = Math.Abs(ax * bx + ay * by + az * bz + aw * bw);
        dot = Math.Clamp(dot, 0.0, 1.0);
        return 2.0 * Math.Acos(dot) * (180.0 / Math.PI);
    }
}

internal static class Program
{
    private const int Frames = 1200;
    private const int SendHz = 20;
    private const int LateJoinFrame = 200;

    private static readonly V3Spec[] V3Tunings =
    {
        new("delta-v3.2", BasisAvatarDeltaRecoveryV3.Options.Default, true),
        new("delta-v3.2-cycle8-r2", new BasisAvatarDeltaRecoveryV3.Options(8, 2, true), true),
        new("delta-v3.2-cycle10-r2", new BasisAvatarDeltaRecoveryV3.Options(10, 2, true), true),
        new("delta-v3.2-cycle10-r4", new BasisAvatarDeltaRecoveryV3.Options(10, 4, true), true),
        new("delta-v3.1-cycle12-r2", new BasisAvatarDeltaRecoveryV3.Options(12, 2, true), true),
        new("delta-v3.1-cycle12-r4", BasisAvatarDeltaRecoveryV3.Options.V31LowOverhead, true),
        new("delta-v3-legacy-cycle8", BasisAvatarDeltaRecoveryV3.Options.LegacyCycle8, false),
    };

    private static readonly Quaternion4Spec[] Quaternion4Tunings =
    {
        new("quat4-upstream", BasisNumerelQuaternion4ArmatureCodec.Options.Upstream),
        new("quat4-upstream-continuous", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous),
        new("quat4-upstream-continuous-minus1", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuousMinus1),
        new("quat4-upstream-continuous-minus2", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuousMinus2),
        new("quat4-upstream-continuous-plus1", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuousPlus1),
        new("quat4-upstream-continuous-plus2", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuousPlus2),
        new("quat4-upstream-continuous-adaptive", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuousAdaptive),
        new("quat4-upstream-continuous-12bit", BasisNumerelQuaternion4ArmatureCodec.Options.UpstreamContinuous12Bit),
        new("quat4-sqrt-0.4-continuous-12bit", BasisNumerelQuaternion4ArmatureCodec.Options.SquareRoot04Continuous12Bit),
        new("quat4-sqrt-nearest-continuous-12bit", BasisNumerelQuaternion4ArmatureCodec.Options.NearestSquareRootContinuous12Bit),
    };

    private static readonly CodecSpec[] Tunings =
    {
        new("reference", BasisNumerelArmatureCodec.Options.NumerelOnly(BasisNumerel.Tuning.Reference)),
        new("sqrt-0.4-reference", BasisNumerelArmatureCodec.Options.NumerelOnly(BasisNumerel.Tuning.SquareRoot04Reference)),
        new("sqrt-nearest-reference", BasisNumerelArmatureCodec.Options.NumerelOnly(BasisNumerel.Tuning.NearestSquareRootReference)),
        new("snap-floor-1gray", BasisNumerelArmatureCodec.Options.NumerelOnly(new BasisNumerel.Tuning(1, -1, false, false))),
        new("snap-nearest-1gray", BasisNumerelArmatureCodec.Options.NumerelOnly(new BasisNumerel.Tuning(1, -1, true, false))),
        new("armature-poc-2gray", BasisNumerelArmatureCodec.Options.NumerelOnly(BasisNumerel.Tuning.ArmaturePoc)),
        new("hybrid-r512-refresh0", new BasisNumerelArmatureCodec.Options(new BasisNumerel.Tuning(1, -1, true, false), true, 512, 0)),
        new("hybrid-r512-refresh2", new BasisNumerelArmatureCodec.Options(new BasisNumerel.Tuning(1, -1, true, false), true, 512, 2)),
        new("hybrid-r512-refresh4", BasisNumerelArmatureCodec.Options.HybridPoc),
        new("hybrid-r512-refresh8", new BasisNumerelArmatureCodec.Options(new BasisNumerel.Tuning(1, -1, true, false), true, 512, 8)),
        new("hybrid-v2-r8", new BasisNumerelArmatureCodec.Options(new BasisNumerel.Tuning(1, -1, true, false), true, 256, 8, 6, 8, false)),
        new("hybrid-v2", BasisNumerelArmatureCodec.Options.HybridV2),
        new("hybrid-v2-r16", new BasisNumerelArmatureCodec.Options(new BasisNumerel.Tuning(1, -1, true, false), true, 256, 16, 6, 8, false)),
    };

    public static int Main(string[] args)
    {
        string outputPath = args.Length > 0 ? args[0] : "numerel-armature-benchmark-results.json";
        var document = new BenchmarkDocument
        {
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            FramesPerScenario = Frames,
            SendRateHz = SendHz,
        };

        BitQuality[] qualities = { BitQuality.VeryLow, BitQuality.Low, BitQuality.Medium, BitQuality.High };
        MotionProfile[] motions = { MotionProfile.Static, MotionProfile.Idle, MotionProfile.Active, MotionProfile.Burst };

        foreach (BitQuality quality in qualities)
        {
            foreach (MotionProfile motion in motions)
            {
                byte[][] frames = GenerateFrames(quality, motion, Frames, fullAvatarMotion: true);
                document.Legacy.Add(RunLegacy(frames, quality, motion));

                foreach (V3Spec tuning in V3Tunings)
                {
                    foreach ((double loss, double reorder) in new[] { (0.0, 0.0), (0.05, 0.0), (0.10, 0.02), (0.20, 0.05) })
                    {
                        document.RecoveryV3.Add(RunV3(frames, quality, motion, tuning, loss, reorder));
                    }
                }

                foreach (Quaternion4Spec tuning in Quaternion4Tunings)
                {
                    foreach ((double loss, double reorder) in new[] { (0.0, 0.0), (0.05, 0.0), (0.10, 0.02), (0.20, 0.05) })
                    {
                        document.Numerel.Add(RunQuaternion4(frames, quality, motion, tuning, loss, reorder));
                    }
                }

                foreach (CodecSpec tuning in Tunings)
                {
                    foreach ((double loss, double reorder) in new[] { (0.0, 0.0), (0.05, 0.0), (0.10, 0.02), (0.20, 0.05) })
                    {
                        document.Numerel.Add(RunNumerel(frames, quality, motion, tuning, loss, reorder));
                    }
                }
            }
        }

        byte[][] cpuFrames = GenerateFrames(BitQuality.High, MotionProfile.Idle, 256, fullAvatarMotion: true);
        foreach (V3Spec tuning in V3Tunings) document.Cpu.Add(RunV3Cpu(cpuFrames, tuning));
        foreach (Quaternion4Spec tuning in Quaternion4Tunings) document.Cpu.Add(RunQuaternion4Cpu(cpuFrames, tuning));
        foreach (CodecSpec tuning in Tunings) document.Cpu.Add(RunCpu(cpuFrames, tuning));

        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputPath, json);
        PrintSummary(document, outputPath);
        return 0;
    }

    private static LegacyResult RunLegacy(byte[][] frames, BitQuality quality, MotionProfile motion)
    {
        int payloadSize = BasisAvatarDeltaCompression.PayloadSize(quality);
        byte[] baseline = new byte[payloadSize];
        byte[] scratch = new byte[BasisAvatarDeltaCompression.MaxDeltaSize(quality)];
        var bodySizes = new List<int>(frames.Length);
        long framedTotal = 0;
        int keyframes = 0, deltas = 0;
        int lastKeyframe = -1000;

        for (int frame = 0; frame < frames.Length; frame++)
        {
            bool keyframe = frame == 0 || frame - lastKeyframe >= 10; // current 500 ms base cadence at 20 Hz
            int body = payloadSize;
            if (!keyframe)
            {
                int delta = BasisAvatarDeltaCompression.BuildDelta(baseline, frames[frame], quality, scratch, 0);
                if (delta < 0 || delta >= payloadSize) keyframe = true;
                else body = delta;
            }

            if (keyframe)
            {
                Buffer.BlockCopy(frames[frame], 0, baseline, 0, payloadSize);
                lastKeyframe = frame;
                body = payloadSize;
                keyframes++;
                framedTotal += body + 3; // id + interval + sequence; channel carries quality
            }
            else
            {
                deltas++;
                framedTotal += body + 5; // delta header + id + interval + seq + baseSeq
            }
            bodySizes.Add(body);
        }

        bodySizes.Sort();
        return new LegacyResult
        {
            Quality = quality.ToString(),
            Motion = motion.ToString(),
            AverageBodyBytes = bodySizes.Average(),
            AverageFramedBytes = framedTotal / (double)frames.Length,
            P95BodyBytes = PercentileSorted(bodySizes, 0.95),
            MaxBodyBytes = bodySizes[^1],
            Keyframes = keyframes,
            Deltas = deltas,
            BytesPerSecond20Hz = framedTotal / (double)frames.Length * SendHz,
        };
    }

    private static NumerelResult RunV3(
        byte[][] frames,
        BitQuality quality,
        MotionProfile motion,
        V3Spec tuning,
        double loss,
        double reorder)
    {
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(quality, tuning.Value);
        var steadyDecoder = new BasisAvatarDeltaRecoveryV3.Decoder(quality, tuning.Value);
        var lateDecoder = new BasisAvatarDeltaRecoveryV3.Decoder(quality, tuning.Value);
        byte[] encodeBuffer = new byte[encoder.MaxBodySize];
        byte[] steadyOutput = new byte[encoder.PayloadSize];
        byte[] lateOutput = new byte[encoder.PayloadSize];
        var sizes = new List<int>(frames.Length);
        var steadyErrors = new ErrorAccumulator(0);
        var lateErrors = new ErrorAccumulator(LateJoinFrame);
        var rng = new Random(StableSeed(quality, motion, tuning.Name, loss, reorder));
        Packet? held = null;
        int delivered = 0;
        int steadyAccepted = 0;
        int lateAccepted = 0;
        long framedTotal = 0;

        void Deliver(Packet packet)
        {
            delivered++;
            if (steadyDecoder.TryDecode(packet.Bytes, 0, packet.Length, packet.Sequence, steadyOutput, out _))
                steadyAccepted++;

            if (packet.FrameIndex >= LateJoinFrame
                && lateDecoder.TryDecode(packet.Bytes, 0, packet.Length, packet.Sequence, lateOutput, out _))
                lateAccepted++;
        }

        for (int frame = 0; frame < frames.Length; frame++)
        {
            byte sequence = (byte)frame;
            byte recoveryRequest = 0;
            if (tuning.RecoveryRequests)
            {
                recoveryRequest |= steadyDecoder.MissingGroupMask;
                if (frame > LateJoinFrame) recoveryRequest |= lateDecoder.MissingGroupMask;
            }
            int length = encoder.Encode(frames[frame], sequence, recoveryRequest, encodeBuffer, 0);
            if (length <= 0) throw new InvalidOperationException("V3 encode failed");
            sizes.Add(length);
            framedTotal += length + 4; // V3 mode + id + interval + sequence; refresh schedule is implicit.

            if (rng.NextDouble() >= loss)
            {
                byte[] packetBytes = new byte[length];
                Buffer.BlockCopy(encodeBuffer, 0, packetBytes, 0, length);
                var packet = new Packet(packetBytes, length, sequence, frame);

                if (held != null)
                {
                    Deliver(packet);
                    Deliver(held);
                    held = null;
                }
                else if (rng.NextDouble() < reorder)
                {
                    held = packet;
                }
                else
                {
                    Deliver(packet);
                }
            }

            // Score what a user would actually see on every offered frame, not only successfully
            // decoded datagrams. Lost or rejected packets therefore measure the held displayed pose.
            if (frame >= 100)
            {
                steadyDecoder.CopyDisplayedPose(steadyOutput);
                steadyErrors.AddFrame(frames[frame], steadyOutput, quality, frame);
            }
            if (frame >= LateJoinFrame)
            {
                lateDecoder.CopyDisplayedPose(lateOutput);
                lateErrors.AddFrame(frames[frame], lateOutput, quality, frame);
            }
        }
        if (held != null) Deliver(held);

        sizes.Sort();
        var steady = steadyErrors.Summarize();
        var late = lateErrors.Summarize();
        return new NumerelResult
        {
            Tuning = tuning.Name,
            Quality = quality.ToString(),
            Motion = motion.ToString(),
            LossPercent = loss * 100,
            ReorderPercent = reorder * 100,
            AverageBodyBytes = sizes.Average(),
            AverageFramedBytes = framedTotal / (double)frames.Length,
            P50BodyBytes = PercentileSorted(sizes, 0.50),
            P95BodyBytes = PercentileSorted(sizes, 0.95),
            P99BodyBytes = PercentileSorted(sizes, 0.99),
            MaxBodyBytes = sizes[^1],
            BytesPerSecond20Hz = framedTotal / (double)frames.Length * SendHz,
            OfferedFrames = frames.Length,
            DeliveredDatagrams = delivered,
            SteadyAcceptedFrames = steadyAccepted,
            LateAcceptedFrames = lateAccepted,
            SteadyMeanAngularErrorDeg = steady.mean,
            SteadyP95AngularErrorDeg = steady.p95,
            SteadyP99AngularErrorDeg = steady.p99,
            SteadyMaxAngularErrorDeg = steady.max,
            LateMeanAngularErrorDeg = late.mean,
            LateP95AngularErrorDeg = late.p95,
            LateP99AngularErrorDeg = late.p99,
            LateMaxAngularErrorDeg = late.max,
            LateJoinStableUnder1DegMs = lateErrors.StableUnder1Ms,
            LateJoinStableUnder025DegMs = lateErrors.StableUnder025Ms,
        };
    }

    private static NumerelResult RunQuaternion4(
        byte[][] frames,
        BitQuality quality,
        MotionProfile motion,
        Quaternion4Spec tuning,
        double loss,
        double reorder)
    {
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(quality, tuning.Value);
        var steadyDecoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, tuning.Value);
        var lateDecoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(quality, tuning.Value);
        byte[] encodeBuffer = new byte[encoder.MaxBodySize];
        byte[] steadyOutput = new byte[encoder.PayloadSize];
        byte[] lateOutput = new byte[encoder.PayloadSize];
        var sizes = new List<int>(frames.Length);
        var steadyErrors = new ErrorAccumulator(0);
        var lateErrors = new ErrorAccumulator(LateJoinFrame);
        var rng = new Random(StableSeed(quality, motion, tuning.Name, loss, reorder));
        Packet? held = null;
        int delivered = 0;
        int steadyAccepted = 0;
        int lateAccepted = 0;
        long framedTotal = 0;

        void Deliver(Packet packet)
        {
            delivered++;
            if (steadyDecoder.TryDecode(packet.Bytes, 0, packet.Length, packet.Sequence, steadyOutput, out _))
                steadyAccepted++;

            if (packet.FrameIndex >= LateJoinFrame
                && lateDecoder.TryDecode(packet.Bytes, 0, packet.Length, packet.Sequence, lateOutput, out _))
                lateAccepted++;
        }

        for (int frame = 0; frame < frames.Length; frame++)
        {
            byte sequence = (byte)frame;
            int length = encoder.Encode(frames[frame], sequence, encodeBuffer, 0);
            if (length <= 0) throw new InvalidOperationException("Quaternion-4 Numerel encode failed");
            sizes.Add(length);
            framedTotal += length + 4;

            if (rng.NextDouble() >= loss)
            {
                byte[] packetBytes = new byte[length];
                Buffer.BlockCopy(encodeBuffer, 0, packetBytes, 0, length);
                var packet = new Packet(packetBytes, length, sequence, frame);

                if (held != null)
                {
                    Deliver(packet);
                    Deliver(held);
                    held = null;
                }
                else if (rng.NextDouble() < reorder)
                {
                    held = packet;
                }
                else
                {
                    Deliver(packet);
                }
            }

            if (frame >= 100)
            {
                steadyDecoder.CopyDisplayedPose(steadyOutput);
                steadyErrors.AddFrame(frames[frame], steadyOutput, quality, frame);
            }
            if (frame >= LateJoinFrame)
            {
                lateDecoder.CopyDisplayedPose(lateOutput);
                lateErrors.AddFrame(frames[frame], lateOutput, quality, frame);
            }
        }
        if (held != null) Deliver(held);

        sizes.Sort();
        var steady = steadyErrors.Summarize();
        var late = lateErrors.Summarize();
        return new NumerelResult
        {
            Tuning = tuning.Name,
            Quality = quality.ToString(),
            Motion = motion.ToString(),
            LossPercent = loss * 100,
            ReorderPercent = reorder * 100,
            AverageBodyBytes = sizes.Average(),
            AverageFramedBytes = framedTotal / (double)frames.Length,
            P50BodyBytes = PercentileSorted(sizes, 0.50),
            P95BodyBytes = PercentileSorted(sizes, 0.95),
            P99BodyBytes = PercentileSorted(sizes, 0.99),
            MaxBodyBytes = sizes[^1],
            BytesPerSecond20Hz = framedTotal / (double)frames.Length * SendHz,
            OfferedFrames = frames.Length,
            DeliveredDatagrams = delivered,
            SteadyAcceptedFrames = steadyAccepted,
            LateAcceptedFrames = lateAccepted,
            SteadyMeanAngularErrorDeg = steady.mean,
            SteadyP95AngularErrorDeg = steady.p95,
            SteadyP99AngularErrorDeg = steady.p99,
            SteadyMaxAngularErrorDeg = steady.max,
            LateMeanAngularErrorDeg = late.mean,
            LateP95AngularErrorDeg = late.p95,
            LateP99AngularErrorDeg = late.p99,
            LateMaxAngularErrorDeg = late.max,
            LateJoinStableUnder1DegMs = lateErrors.StableUnder1Ms,
            LateJoinStableUnder025DegMs = lateErrors.StableUnder025Ms,
        };
    }

    private static NumerelResult RunNumerel(
        byte[][] frames,
        BitQuality quality,
        MotionProfile motion,
        CodecSpec tuning,
        double loss,
        double reorder)
    {
        var encoder = new BasisNumerelArmatureCodec.Encoder(quality, tuning.Value);
        var steadyDecoder = new BasisNumerelArmatureCodec.Decoder(quality, tuning.Value);
        var lateDecoder = new BasisNumerelArmatureCodec.Decoder(quality, tuning.Value);
        byte[] encodeBuffer = new byte[encoder.MaxBodySize];
        byte[] steadyOutput = new byte[encoder.PayloadSize];
        byte[] lateOutput = new byte[encoder.PayloadSize];
        var sizes = new List<int>(frames.Length);
        var steadyErrors = new ErrorAccumulator(0);
        var lateErrors = new ErrorAccumulator(LateJoinFrame);
        var rng = new Random(StableSeed(quality, motion, tuning.Name, loss, reorder));
        Packet? held = null;
        int delivered = 0;
        int steadyAccepted = 0;
        int lateAccepted = 0;
        long framedTotal = 0;

        void Deliver(Packet packet)
        {
            delivered++;
            if (steadyDecoder.TryDecode(packet.Bytes, 0, packet.Length, packet.Sequence, steadyOutput, out _))
                steadyAccepted++;

            if (packet.FrameIndex >= LateJoinFrame
                && lateDecoder.TryDecode(packet.Bytes, 0, packet.Length, packet.Sequence, lateOutput, out _))
                lateAccepted++;
        }

        for (int frame = 0; frame < frames.Length; frame++)
        {
            byte sequence = (byte)frame;
            int length = encoder.Encode(frames[frame], sequence, encodeBuffer, 0);
            if (length <= 0) throw new InvalidOperationException("Numerel encode failed");
            sizes.Add(length);
            framedTotal += length + 4; // numerel header + id + interval + sequence; no base sequence

            if (rng.NextDouble() >= loss)
            {
                byte[] packetBytes = new byte[length];
                Buffer.BlockCopy(encodeBuffer, 0, packetBytes, 0, length);
                var packet = new Packet(packetBytes, length, sequence, frame);

                if (held != null)
                {
                    // Deliver the newer packet first, then the held older packet. The decoder must
                    // accept the first and reject the stale second without mutating state.
                    Deliver(packet);
                    Deliver(held);
                    held = null;
                }
                else if (rng.NextDouble() < reorder)
                {
                    held = packet;
                }
                else
                {
                    Deliver(packet);
                }
            }

            if (frame >= 100)
            {
                steadyDecoder.CopyDisplayedPose(steadyOutput);
                steadyErrors.AddFrame(frames[frame], steadyOutput, quality, frame);
            }
            if (frame >= LateJoinFrame)
            {
                lateDecoder.CopyDisplayedPose(lateOutput);
                lateErrors.AddFrame(frames[frame], lateOutput, quality, frame);
            }
        }
        if (held != null) Deliver(held);

        sizes.Sort();
        var steady = steadyErrors.Summarize();
        var late = lateErrors.Summarize();
        return new NumerelResult
        {
            Tuning = tuning.Name,
            Quality = quality.ToString(),
            Motion = motion.ToString(),
            LossPercent = loss * 100,
            ReorderPercent = reorder * 100,
            AverageBodyBytes = sizes.Average(),
            AverageFramedBytes = framedTotal / (double)frames.Length,
            P50BodyBytes = PercentileSorted(sizes, 0.50),
            P95BodyBytes = PercentileSorted(sizes, 0.95),
            P99BodyBytes = PercentileSorted(sizes, 0.99),
            MaxBodyBytes = sizes[^1],
            BytesPerSecond20Hz = framedTotal / (double)frames.Length * SendHz,
            OfferedFrames = frames.Length,
            DeliveredDatagrams = delivered,
            SteadyAcceptedFrames = steadyAccepted,
            LateAcceptedFrames = lateAccepted,
            SteadyMeanAngularErrorDeg = steady.mean,
            SteadyP95AngularErrorDeg = steady.p95,
            SteadyP99AngularErrorDeg = steady.p99,
            SteadyMaxAngularErrorDeg = steady.max,
            LateMeanAngularErrorDeg = late.mean,
            LateP95AngularErrorDeg = late.p95,
            LateP99AngularErrorDeg = late.p99,
            LateMaxAngularErrorDeg = late.max,
            LateJoinStableUnder1DegMs = lateErrors.StableUnder1Ms,
            LateJoinStableUnder025DegMs = lateErrors.StableUnder025Ms,
        };
    }

    private static CpuResult RunV3Cpu(byte[][] frames, V3Spec tuning)
    {
        const int iterations = 100_000;
        var encoder = new BasisAvatarDeltaRecoveryV3.Encoder(BitQuality.High, tuning.Value);
        byte[] buffer = new byte[encoder.MaxBodySize];

        for (int i = 0; i < 2000; i++)
            encoder.Encode(frames[i & 255], (byte)i, buffer, 0);
        encoder.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            encoder.Encode(frames[i & 255], (byte)i, buffer, 0);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long encodeAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        double encodeNs = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;

        var packetEncoder = new BasisAvatarDeltaRecoveryV3.Encoder(BitQuality.High, tuning.Value);
        byte[][] packets = new byte[256][];
        int[] lengths = new int[256];
        for (int i = 0; i < packets.Length; i++)
        {
            byte[] p = new byte[packetEncoder.MaxBodySize];
            lengths[i] = packetEncoder.Encode(frames[i], (byte)i, p, 0);
            packets[i] = p;
        }

        var decoder = new BasisAvatarDeltaRecoveryV3.Decoder(BitQuality.High, tuning.Value);
        byte[] output = new byte[decoder.PayloadSize];
        for (int i = 0; i < 2000; i++)
        {
            int index = i & 255;
            if (index == 0 && i != 0) decoder.Reset();
            if (!decoder.TryDecode(packets[index], 0, lengths[index], (byte)index, output, out _))
                throw new InvalidOperationException("V3 warmup decode failed");
        }
        decoder.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int index = i & 255;
            if (index == 0 && i != 0) decoder.Reset();
            if (!decoder.TryDecode(packets[index], 0, lengths[index], (byte)index, output, out _))
                throw new InvalidOperationException("V3 timed decode failed");
        }
        elapsed = Stopwatch.GetTimestamp() - start;
        long decodeAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        double decodeNs = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;

        return new CpuResult
        {
            Tuning = tuning.Name,
            EncodeNanosecondsPerFrame = encodeNs,
            DecodeNanosecondsPerFrame = decodeNs,
            EncodeAllocatedBytes = encodeAlloc,
            DecodeAllocatedBytes = decodeAlloc,
        };
    }

    private static CpuResult RunQuaternion4Cpu(byte[][] frames, Quaternion4Spec tuning)
    {
        const int iterations = 100_000;
        var encoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(BitQuality.High, tuning.Value);
        byte[] buffer = new byte[encoder.MaxBodySize];

        for (int i = 0; i < 2000; i++)
            if (encoder.Encode(frames[i & 255], (byte)i, buffer, 0) <= 0)
                throw new InvalidOperationException("Quaternion-4 warmup encode failed");
        encoder.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            if (encoder.Encode(frames[i & 255], (byte)i, buffer, 0) <= 0)
                throw new InvalidOperationException("Quaternion-4 timed encode failed");
        long encodeElapsed = Stopwatch.GetTimestamp() - start;
        long encodeAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        double encodeNs = encodeElapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;

        var packetEncoder = new BasisNumerelQuaternion4ArmatureCodec.Encoder(BitQuality.High, tuning.Value);
        byte[][] packets = new byte[256][];
        int[] lengths = new int[256];
        for (int i = 0; i < packets.Length; i++)
        {
            byte[] p = new byte[packetEncoder.MaxBodySize];
            lengths[i] = packetEncoder.Encode(frames[i], (byte)i, p, 0);
            if (lengths[i] <= 0)
                throw new InvalidOperationException("Quaternion-4 corpus encode failed");
            packets[i] = p;
        }

        var decoder = new BasisNumerelQuaternion4ArmatureCodec.Decoder(BitQuality.High, tuning.Value);
        byte[] output = new byte[decoder.PayloadSize];
        for (int i = 0; i < 2000; i++)
        {
            int index = i & 255;
            if (index == 0 && i != 0) decoder.Reset();
            if (!decoder.TryDecode(packets[index], 0, lengths[index], (byte)index, output, out _))
                throw new InvalidOperationException("Quaternion-4 warmup decode failed");
        }
        decoder.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int index = i & 255;
            if (index == 0 && i != 0) decoder.Reset();
            if (!decoder.TryDecode(packets[index], 0, lengths[index], (byte)index, output, out _))
                throw new InvalidOperationException("Quaternion-4 timed decode failed");
        }
        long decodeElapsed = Stopwatch.GetTimestamp() - start;
        long decodeAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        double decodeNs = decodeElapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;

        return new CpuResult
        {
            Tuning = tuning.Name,
            EncodeNanosecondsPerFrame = encodeNs,
            DecodeNanosecondsPerFrame = decodeNs,
            EncodeAllocatedBytes = encodeAlloc,
            DecodeAllocatedBytes = decodeAlloc,
        };
    }

    private static CpuResult RunCpu(byte[][] frames, CodecSpec tuning)
    {
        const int iterations = 100_000;
        var encoder = new BasisNumerelArmatureCodec.Encoder(BitQuality.High, tuning.Value);
        byte[] buffer = new byte[encoder.MaxBodySize];

        for (int i = 0; i < 2000; i++)
            encoder.Encode(frames[i & 255], (byte)i, buffer, 0);
        encoder.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
            encoder.Encode(frames[i & 255], (byte)i, buffer, 0);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long encodeAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

        var packetEncoder = new BasisNumerelArmatureCodec.Encoder(BitQuality.High, tuning.Value);
        byte[][] packets = new byte[256][];
        int[] lengths = new int[256];
        for (int i = 0; i < packets.Length; i++)
        {
            byte[] p = new byte[packetEncoder.MaxBodySize];
            lengths[i] = packetEncoder.Encode(frames[i], (byte)i, p, 0);
            packets[i] = p;
        }

        var decoder = new BasisNumerelArmatureCodec.Decoder(BitQuality.High, tuning.Value);
        byte[] output = new byte[decoder.PayloadSize];
        for (int i = 0; i < 2000; i++)
        {
            int index = i & 255;
            // The packet corpus is a captured 0..255 stateful stream. Replaying packet zero
            // after packet 255 is not a real sequence wrap (a live encoder would continue its
            // state), so reset at corpus boundaries rather than feeding an invalid temporal base.
            if (index == 0 && i != 0) decoder.Reset();
            if (!decoder.TryDecode(packets[index], 0, lengths[index], (byte)index, output, out _))
                throw new InvalidOperationException("Warmup decode failed");
        }
        decoder.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++)
        {
            int index = i & 255;
            if (index == 0 && i != 0) decoder.Reset();
            if (!decoder.TryDecode(packets[index], 0, lengths[index], (byte)index, output, out _))
                throw new InvalidOperationException("Timed decode failed");
        }
        elapsed = Stopwatch.GetTimestamp() - start;
        long decodeAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;

        double decodeNs = elapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;
        // Re-run the encode timing conversion because elapsed now contains decode time.
        encoder.Reset();
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) encoder.Encode(frames[i & 255], (byte)i, buffer, 0);
        long encodeElapsed = Stopwatch.GetTimestamp() - start;
        double encodeNs = encodeElapsed * 1_000_000_000.0 / Stopwatch.Frequency / iterations;

        return new CpuResult
        {
            Tuning = tuning.Name,
            EncodeNanosecondsPerFrame = encodeNs,
            DecodeNanosecondsPerFrame = decodeNs,
            EncodeAllocatedBytes = encodeAlloc,
            DecodeAllocatedBytes = decodeAlloc,
        };
    }

    private static byte[][] GenerateFrames(BitQuality quality, MotionProfile motion, int count, bool fullAvatarMotion)
    {
        byte[][] frames = new byte[count][];
        byte[] bpc = BasisBoneRotationCompression.GetBpcTable(quality);
        int[] offsets = new int[bpc.Length];
        BasisBoneRotationCompression.ComputeBitOffsets(bpc, offsets);
        int positionBytes = BasisAvatarBitPacking.PositionBytes(quality);
        int rotationBytes = BasisBoneRotationCompression.RotationBytes(quality);
        int tail = positionBytes + rotationBytes;

        for (int frame = 0; frame < count; frame++)
        {
            double t = frame / (double)SendHz;
            byte[] payload = new byte[BasisBoneRotationCompression.ConvertToSize(quality)];
            WritePosition(payload, quality, t, fullAvatarMotion && motion != MotionProfile.Static);

            for (int bone = 0; bone < bpc.Length; bone++)
            {
                GetPose(bone, t, motion, out float x, out float y, out float z, out float w);
                ulong packed = BasisBoneRotationCompression.EncodeSmallestThree(
                    x, y, z, w, bpc[bone], BasisBoneRotationCompression.MAX_COMPONENT[bone]);
                BasisBoneRotationCompression.WriteBits(payload, positionBytes * 8 + offsets[bone], packed, 2 + 3 * bpc[bone]);
            }

            payload[tail] = 0x00;
            payload[tail + 1] = 0x3C;
            double bodyYaw = fullAvatarMotion && motion != MotionProfile.Static ? 8.0 * Math.Sin(t * 0.7) : 0.0;
            AxisAngle(0, 1, 0, (float)bodyYaw, out float bx, out float by, out float bz, out float bw);
            WriteCompressedQuat(payload, tail + 2, bx, by, bz, bw);

            int hipsDelta = tail + 2 + BasisBoneRotationCompression.WriteRotation;
            short hx = (short)(fullAvatarMotion ? Math.Round(Math.Sin(t * 1.1) * 600) : 0);
            short hy = (short)(fullAvatarMotion ? Math.Round(Math.Sin(t * 0.8) * 300) : 0);
            short hz = (short)(fullAvatarMotion ? Math.Round(Math.Cos(t * 1.3) * 500) : 0);
            WriteInt16(payload, hipsDelta, hx); WriteInt16(payload, hipsDelta + 2, hy); WriteInt16(payload, hipsDelta + 4, hz);
            WriteCompressedQuat(payload, hipsDelta + 6, bx, by, bz, bw);

            int endBytes = BasisBoneRotationCompression.EndEffectorBytes(quality);
            if (endBytes > 0)
            {
                int end = tail + BasisBoneRotationCompression.TailBytes;
                for (int i = 0; i < endBytes; i++)
                    payload[end + i] = fullAvatarMotion && motion != MotionProfile.Static
                        ? (byte)Math.Round((Math.Sin(t * (0.4 + (i % 7) * 0.03) + i) + 1.0) * 127.5)
                        : (byte)(i * 13);
            }
            frames[frame] = payload;
        }
        return frames;
    }

    private static void GetPose(int bone, double t, MotionProfile motion, out float x, out float y, out float z, out float w)
    {
        float baseAngle = bone switch
        {
            5 or 6 => -68f,
            9 => 18f,
            10 => -18f,
            >= 21 and <= 30 => 20f,
            >= 31 and <= 40 => 30f,
            >= 41 => 15f,
            _ => 0f,
        };

        float amplitude = motion switch
        {
            MotionProfile.Static => 0f,
            MotionProfile.Idle => bone >= 21 ? 7f : 2.5f,
            MotionProfile.Active => bone >= 21 ? 32f : 24f,
            MotionProfile.Burst => bone >= 21 ? 38f : 30f,
            _ => 0f,
        };
        double frequency = motion switch
        {
            MotionProfile.Static => 0,
            MotionProfile.Idle => 0.12 + (bone % 7) * 0.025,
            MotionProfile.Active => 0.45 + (bone % 9) * 0.07,
            MotionProfile.Burst => 0.55 + (bone % 11) * 0.08,
            _ => 0,
        };
        float angle = baseAngle + amplitude * (float)Math.Sin(t * frequency * Math.PI * 2.0 + bone * 0.61);
        if (motion == MotionProfile.Burst && ((int)(t * 2.0) % 13) == 7)
            angle += (bone % 2 == 0 ? 1f : -1f) * 45f;

        switch (bone % 3)
        {
            case 0: AxisAngle(1, 0, 0, angle, out x, out y, out z, out w); break;
            case 1: AxisAngle(0, 1, 0, angle, out x, out y, out z, out w); break;
            default: AxisAngle(0, 0, 1, angle, out x, out y, out z, out w); break;
        }
    }

    private static void WritePosition(byte[] payload, BitQuality quality, double t, bool moving)
    {
        float x = moving ? (float)(Math.Sin(t * 0.4) * 2.0) : 0f;
        float y = moving ? 1.0f + (float)(Math.Sin(t * 0.7) * 0.1) : 1f;
        float z = moving ? (float)(Math.Cos(t * 0.4) * 2.0) : 0f;
        if (quality == BitQuality.High)
        {
            WriteFloat(payload, 0, x); WriteFloat(payload, 4, y); WriteFloat(payload, 8, z);
        }
        else
        {
            WriteInt24(payload, 0, (int)Math.Round(x * 1000));
            WriteInt24(payload, 3, (int)Math.Round(y * 1000));
            WriteInt24(payload, 6, (int)Math.Round(z * 1000));
        }
    }

    private static void AxisAngle(float ax, float ay, float az, float degrees, out float x, out float y, out float z, out float w)
    {
        float length = MathF.Sqrt(ax * ax + ay * ay + az * az);
        if (length < 1e-6f) { x = y = z = 0; w = 1; return; }
        float half = degrees * (MathF.PI / 180f) * 0.5f;
        float s = MathF.Sin(half) / length;
        x = ax * s; y = ay * s; z = az * s; w = MathF.Cos(half);
    }

    private static void WriteCompressedQuat(byte[] dst, int offset, float qx, float qy, float qz, float qw)
    {
        float ax = MathF.Abs(qx), ay = MathF.Abs(qy), az = MathF.Abs(qz), aw = MathF.Abs(qw);
        int largest = 0; float max = ax;
        if (ay > max) { largest = 1; max = ay; }
        if (az > max) { largest = 2; max = az; }
        if (aw > max) largest = 3;
        float sign = largest switch { 0 => qx, 1 => qy, 2 => qz, _ => qw };
        if (sign < 0) { qx = -qx; qy = -qy; qz = -qz; qw = -qw; }
        float a, b, c;
        switch (largest)
        {
            case 0: a = qy; b = qz; c = qw; break;
            case 1: a = qx; b = qz; c = qw; break;
            case 2: a = qx; b = qy; c = qw; break;
            default: a = qx; b = qy; c = qz; break;
        }
        dst[offset] = (byte)largest;
        WriteUInt16(dst, offset + 1, QuantizeSmall(a));
        WriteUInt16(dst, offset + 3, QuantizeSmall(b));
        WriteUInt16(dst, offset + 5, QuantizeSmall(c));
    }

    private static ushort QuantizeSmall(float value)
    {
        const float invSqrt2 = 0.70710678118f;
        float normalized = Math.Clamp((value + invSqrt2) / (2f * invSqrt2), 0f, 1f);
        return (ushort)MathF.Round(normalized * ushort.MaxValue);
    }

    private static int StableSeed(BitQuality quality, MotionProfile motion, string tuning, double loss, double reorder)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (int)quality;
            hash = hash * 31 + (int)motion;
            foreach (char c in tuning) hash = hash * 31 + c;
            hash = hash * 31 + (int)Math.Round(loss * 1000);
            hash = hash * 31 + (int)Math.Round(reorder * 1000);
            return hash;
        }
    }

    private static void PrintSummary(BenchmarkDocument document, string outputPath)
    {
        Console.WriteLine($"Numerel armature benchmark written to {outputPath}");
        Console.WriteLine("High / Idle summary:");
        LegacyResult legacy = document.Legacy.Single(x => x.Quality == "High" && x.Motion == "Idle");
        Console.WriteLine($"  legacy keyframe+delta: {legacy.AverageFramedBytes:F1} B/frame, {legacy.BytesPerSecond20Hz:F0} B/s, keyframes={legacy.Keyframes}");
        foreach (NumerelResult result in document.RecoveryV3.Where(x => x.Quality == "High" && x.Motion == "Idle" && x.LossPercent is 0 or 10))
        {
            Console.WriteLine($"  {result.Tuning,-22} loss={result.LossPercent,2:F0}% {result.AverageFramedBytes,6:F1} B/frame " +
                              $"display-p95={result.SteadyP95AngularErrorDeg,6:F3}deg late<1deg={result.LateJoinStableUnder1DegMs?.ToString("F0") ?? "never"}ms");
        }
        foreach (NumerelResult result in document.Numerel.Where(x => x.Quality == "High" && x.Motion == "Idle" && x.LossPercent is 0 or 10))
        {
            Console.WriteLine($"  {result.Tuning,-22} loss={result.LossPercent,2:F0}% {result.AverageFramedBytes,6:F1} B/frame " +
                              $"p95err={result.SteadyP95AngularErrorDeg,6:F3}deg late<1deg={result.LateJoinStableUnder1DegMs?.ToString("F0") ?? "never"}ms");
        }
        Console.WriteLine("CPU High / Idle:");
        foreach (CpuResult cpu in document.Cpu)
            Console.WriteLine($"  {cpu.Tuning,-22} enc={cpu.EncodeNanosecondsPerFrame:F0}ns dec={cpu.DecodeNanosecondsPerFrame:F0}ns alloc={cpu.EncodeAllocatedBytes + cpu.DecodeAllocatedBytes}B");
    }

    private static int PercentileSorted(List<int> sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

    private static double PercentileSorted(List<double> sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1)];

    private static void WriteFloat(byte[] dst, int offset, float value)
        => WriteInt32(dst, offset, BitConverter.SingleToInt32Bits(value));

    private static void WriteInt16(byte[] dst, int offset, short value) => WriteUInt16(dst, offset, unchecked((ushort)value));
    private static void WriteUInt16(byte[] dst, int offset, ushort value)
    {
        dst[offset] = (byte)value; dst[offset + 1] = (byte)(value >> 8);
    }
    private static void WriteInt24(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)value; dst[offset + 1] = (byte)(value >> 8); dst[offset + 2] = (byte)(value >> 16);
    }
    private static void WriteInt32(byte[] dst, int offset, int value)
    {
        dst[offset] = (byte)value; dst[offset + 1] = (byte)(value >> 8); dst[offset + 2] = (byte)(value >> 16); dst[offset + 3] = (byte)(value >> 24);
    }
}
