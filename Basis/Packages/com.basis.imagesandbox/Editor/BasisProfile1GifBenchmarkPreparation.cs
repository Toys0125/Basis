using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Basis.ImagePickup;
using UnityEditor;
using UnityEngine;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1GifBenchmarkPreparation
    {
        private const string CacheFormat = "BasisProfile1GifFullCanvasCacheV1";

        internal static bool TryConvertSynchronously(
            string gifPath,
            out byte[] profile1,
            out string error
        )
        {
            profile1 = null;
            error = null;
            try
            {
                byte[] source = File.ReadAllBytes(gifPath);
                using var request = BasisBurstGifDecoder.Schedule(source);
                using BasisBurstGifDecodeResult result = request.Complete();
                if (result == null || !result.Ok || result.Animation == null)
                {
                    error = "GIF decode failed: " + (result?.Error ?? "unknown error");
                    return false;
                }
                if (!TryBuildTimeline(gifPath, result.Animation, out byte[] timeline, out error))
                    return false;
                EncodeResult encoded = EncodeTimeline(timeline);
                if (!encoded.Ok)
                {
                    error = "GIF Profile 1 encode failed: " + encoded.Error;
                    return false;
                }
                profile1 = encoded.Profile1;
                return true;
            }
            catch (Exception exception)
            {
                error = "GIF benchmark preparation failed: " + exception.Message;
                return false;
            }
        }

        public static async Task<GifPreparationResult> ConvertAsync(
            string[] gifPaths,
            string outputRoot,
            Action<int, int, string, string> progress,
            CancellationToken cancellationToken
        )
        {
            var convertedByOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var errorsByOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var metricsByOriginal = new Dictionary<string, GifPreparationMetrics>(StringComparer.OrdinalIgnoreCase);
            if (gifPaths == null || gifPaths.Length == 0)
                return GifPreparationResult.Success(convertedByOriginal, errorsByOriginal, metricsByOriginal);

            string cacheRoot = Path.Combine(outputRoot, "gif-profile1-cache");
            Directory.CreateDirectory(cacheRoot);

            try
            {
                for (int i = 0; i < gifPaths.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string gifPath = gifPaths[i];
                    long workingSetBefore = GetCurrentWorkingSetBytes();
                    using var memorySampler = new WorkingSetSampler();
                    memorySampler.Start();
                    try
                    {
                        progress?.Invoke(i, gifPaths.Length, Path.GetFileName(gifPath), "Checking cache");
                        string cacheKey = await Task.Run(() => ComputeCacheKey(gifPath), cancellationToken);
                        string stem = SanitizeFileName(Path.GetFileNameWithoutExtension(gifPath));
                        string jxlPath = Path.Combine(cacheRoot, cacheKey + "_" + stem + ".jxl");
                        if (File.Exists(jxlPath) && new FileInfo(jxlPath).Length > 0)
                        {
                            convertedByOriginal[gifPath] = jxlPath;
                            memorySampler.Stop();
                            long cacheWorkingSetAfter = GetCurrentWorkingSetBytes();
                            metricsByOriginal[gifPath] = new GifPreparationMetrics(
                                true,
                                0,
                                0,
                                0,
                                "BasisBurstGifDecoder + editor-native libjxl",
                                workingSetBefore,
                                cacheWorkingSetAfter,
                                memorySampler.PeakBytes,
                                Math.Max(0, memorySampler.PeakBytes - workingSetBefore)
                            );
                            progress?.Invoke(i + 1, gifPaths.Length, Path.GetFileName(gifPath), "Cache hit");
                            continue;
                        }

                        byte[] source = await Task.Run(() => File.ReadAllBytes(gifPath), cancellationToken);
                        progress?.Invoke(i, gifPaths.Length, Path.GetFileName(gifPath), "Decoding GIF");
                        var decodeStopwatch = Stopwatch.StartNew();
                        using var request = BasisBurstGifDecoder.Schedule(source);
                        using BasisBurstGifDecodeResult result = await WaitForDecodeAsync(request, cancellationToken);
                        decodeStopwatch.Stop();
                        if (result == null || !result.Ok || result.Animation == null)
                        {
                            string decodeError = result?.Error ?? "unknown error";
                            errorsByOriginal[gifPath] = "GIF decode failed: " + decodeError;
                            progress?.Invoke(i + 1, gifPaths.Length, Path.GetFileName(gifPath), "Failed");
                            continue;
                        }

                        if (!TryBuildTimeline(gifPath, result.Animation, out byte[] timeline, out string timelineError))
                        {
                            errorsByOriginal[gifPath] = timelineError;
                            progress?.Invoke(i + 1, gifPaths.Length, Path.GetFileName(gifPath), "Failed");
                            continue;
                        }

                        progress?.Invoke(i, gifPaths.Length, Path.GetFileName(gifPath), "Encoding JPEG XL");
                        var encodeStopwatch = Stopwatch.StartNew();
                        EncodeResult encoded = await Task.Run(
                            () => EncodeTimeline(timeline),
                            cancellationToken
                        );
                        encodeStopwatch.Stop();
                        if (!encoded.Ok)
                        {
                            errorsByOriginal[gifPath] = "GIF Profile 1 encode failed: " + encoded.Error;
                            progress?.Invoke(i + 1, gifPaths.Length, Path.GetFileName(gifPath), "Failed");
                            continue;
                        }

                        string temporaryPath = jxlPath + ".tmp";
                        await Task.Run(() =>
                        {
                            if (File.Exists(temporaryPath))
                                File.Delete(temporaryPath);
                            File.WriteAllBytes(temporaryPath, encoded.Profile1);
                            File.Move(temporaryPath, jxlPath);
                        }, cancellationToken);
                        convertedByOriginal[gifPath] = jxlPath;
                        memorySampler.Stop();
                        long workingSetAfter = GetCurrentWorkingSetBytes();
                        metricsByOriginal[gifPath] = new GifPreparationMetrics(
                            false,
                            decodeStopwatch.Elapsed.TotalMilliseconds,
                            encodeStopwatch.Elapsed.TotalMilliseconds,
                            timeline.LongLength,
                            "BasisBurstGifDecoder + editor-native libjxl",
                            workingSetBefore,
                            workingSetAfter,
                            memorySampler.PeakBytes,
                            Math.Max(0, memorySampler.PeakBytes - workingSetBefore)
                        );
                        progress?.Invoke(i + 1, gifPaths.Length, Path.GetFileName(gifPath), "Cached");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        errorsByOriginal[gifPath] = "GIF benchmark preparation failed: " + exception.Message;
                        progress?.Invoke(i + 1, gifPaths.Length, Path.GetFileName(gifPath), "Failed");
                    }
                }
                return GifPreparationResult.Success(convertedByOriginal, errorsByOriginal, metricsByOriginal);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return GifPreparationResult.Failure("GIF benchmark preparation failed: " + exception.Message);
            }
        }

        private static Task<BasisBurstGifDecodeResult> WaitForDecodeAsync(
            BasisBurstGifDecodeRequest request,
            CancellationToken cancellationToken
        )
        {
            var completion = new TaskCompletionSource<BasisBurstGifDecodeResult>();
            void Poll()
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    EditorApplication.update -= Poll;
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }
                try
                {
                    if (!request.TryComplete(out BasisBurstGifDecodeResult result))
                        return;
                    EditorApplication.update -= Poll;
                    completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    EditorApplication.update -= Poll;
                    completion.TrySetException(exception);
                }
            }
            EditorApplication.update += Poll;
            return completion.Task;
        }

        private static EncodeResult EncodeTimeline(byte[] timeline)
        {
            bool ok = BasisProfile1EditorNative.TryEncodeTimeline(timeline, out byte[] profile1, out string error);
            return new EncodeResult(ok, profile1, error);
        }

        private static bool TryBuildTimeline(
            string gifPath,
            BasisAnimatedImageData animation,
            out byte[] timeline,
            out string error
        )
        {
            timeline = null;
            error = null;
            if (animation.TotalPlayCount < 0 || animation.TotalPlayCount > uint.MaxValue)
            {
                error = "GIF loop count cannot be represented by Profile 1: " + gifPath;
                return false;
            }

            int width = animation.CanvasWidth;
            int height = animation.CanvasHeight;
            int canvasPixels = checked(width * height);
            var canvas = new Color32[canvasPixels];
            var previous = animation.RequiresPreviousCanvas ? new Color32[canvasPixels] : null;
            Fill(canvas, animation.BackgroundColor);

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(new byte[] { (byte)'B', (byte)'P', (byte)'1', (byte)'G', (byte)'I', (byte)'F', (byte)'0', (byte)'1' });
            writer.Write((uint)width);
            writer.Write((uint)height);
            writer.Write((uint)animation.FrameCount);
            writer.Write((uint)animation.TotalPlayCount);
            for (int i = 0; i < animation.FrameCount; i++)
            {
                long duration = animation.GetFrame(i).DurationMicroseconds;
                if (duration <= 0 || duration > uint.MaxValue)
                {
                    error = "GIF frame duration cannot be represented by Profile 1: " + gifPath;
                    return false;
                }
                writer.Write((uint)duration);
            }

            BasisAnimatedImageFrame previousFrame = default;
            for (int frameIndex = 0; frameIndex < animation.FrameCount; frameIndex++)
            {
                if (frameIndex > 0)
                    ApplyDisposal(canvas, previous, previousFrame, animation.BackgroundColor, width);

                BasisAnimatedImageFrame frame = animation.GetFrame(frameIndex);
                if (frame.Disposal == BasisAnimationDisposal.Previous)
                    CopyRect(canvas, previous, frame, width);

                Color32[] patch = animation.CopyFramePixelsToManaged(frameIndex);
                DrawPatch(canvas, patch, frame, width);
                WriteTopDownRgba(writer, canvas, width, height);
                previousFrame = frame;
            }
            writer.Flush();
            timeline = stream.ToArray();
            return true;
        }

        private static void ApplyDisposal(
            Color32[] canvas,
            Color32[] previous,
            BasisAnimatedImageFrame frame,
            Color32 background,
            int canvasWidth
        )
        {
            if (frame.Disposal == BasisAnimationDisposal.None)
                return;
            if (frame.Disposal == BasisAnimationDisposal.Previous)
            {
                CopyRect(previous, canvas, frame, canvasWidth);
                return;
            }
            for (int y = 0; y < frame.Height; y++)
            {
                int offset = (frame.Y + y) * canvasWidth + frame.X;
                for (int x = 0; x < frame.Width; x++)
                    canvas[offset + x] = background;
            }
        }

        private static void DrawPatch(Color32[] canvas, Color32[] patch, BasisAnimatedImageFrame frame, int canvasWidth)
        {
            int source = 0;
            for (int y = 0; y < frame.Height; y++)
            {
                int destination = (frame.Y + y) * canvasWidth + frame.X;
                for (int x = 0; x < frame.Width; x++, source++, destination++)
                {
                    Color32 pixel = patch[source];
                    if (frame.Blend == BasisAnimationBlend.Source || pixel.a == byte.MaxValue)
                    {
                        canvas[destination] = pixel;
                    }
                    else if (pixel.a != 0)
                    {
                        canvas[destination] = AlphaOver(pixel, canvas[destination]);
                    }
                }
            }
        }

        private static Color32 AlphaOver(Color32 source, Color32 destination)
        {
            int sa = source.a;
            int da = destination.a;
            int inverse = 255 - sa;
            int outA = sa + ((da * inverse + 127) / 255);
            if (outA <= 0)
                return new Color32(0, 0, 0, 0);

            int dr = (destination.r * da * inverse + 127) / 255;
            int dg = (destination.g * da * inverse + 127) / 255;
            int db = (destination.b * da * inverse + 127) / 255;
            int r = ((source.r * sa + dr) + outA / 2) / outA;
            int g = ((source.g * sa + dg) + outA / 2) / outA;
            int b = ((source.b * sa + db) + outA / 2) / outA;
            return new Color32((byte)r, (byte)g, (byte)b, (byte)outA);
        }

        private static void CopyRect(Color32[] source, Color32[] destination, BasisAnimatedImageFrame frame, int canvasWidth)
        {
            if (source == null || destination == null)
                return;
            for (int y = 0; y < frame.Height; y++)
            {
                int offset = (frame.Y + y) * canvasWidth + frame.X;
                Array.Copy(source, offset, destination, offset, frame.Width);
            }
        }

        private static void Fill(Color32[] pixels, Color32 color)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
        }

        private static void WriteTopDownRgba(BinaryWriter writer, Color32[] canvas, int width, int height)
        {
            for (int y = height - 1; y >= 0; y--)
            {
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 pixel = canvas[offset + x];
                    writer.Write(pixel.r);
                    writer.Write(pixel.g);
                    writer.Write(pixel.b);
                    writer.Write(pixel.a);
                }
            }
        }

        private static string ComputeCacheKey(string gifPath)
        {
            using var sha = SHA256.Create();
            byte[] identity = Encoding.UTF8.GetBytes(
                CacheFormat
                    + "|profile=" + BasisJpegXlProfile1.ProfileVersion
                    + "|libjxl=" + BasisProfile1SandboxDecoder.LibJxlCommit
            );
            sha.TransformBlock(identity, 0, identity.Length, identity, 0);
            using (FileStream stream = File.OpenRead(gifPath))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }

        private readonly struct EncodeResult
        {
            public readonly bool Ok;
            public readonly byte[] Profile1;
            public readonly string Error;

            public EncodeResult(bool ok, byte[] profile1, string error)
            {
                Ok = ok;
                Profile1 = profile1;
                Error = error;
            }
        }

        internal readonly struct GifPreparationMetrics
        {
            public readonly bool CacheHit;
            public readonly double DecodeMilliseconds;
            public readonly double EncodeMilliseconds;
            public readonly long TimelineBytes;
            public readonly string Backend;
            public readonly long WorkingSetBeforeBytes;
            public readonly long WorkingSetAfterBytes;
            public readonly long WorkingSetPeakBytes;
            public readonly long WorkingSetPeakDeltaBytes;

            public GifPreparationMetrics(
                bool cacheHit,
                double decodeMilliseconds,
                double encodeMilliseconds,
                long timelineBytes,
                string backend,
                long workingSetBeforeBytes,
                long workingSetAfterBytes,
                long workingSetPeakBytes,
                long workingSetPeakDeltaBytes)
            {
                CacheHit = cacheHit;
                DecodeMilliseconds = decodeMilliseconds;
                EncodeMilliseconds = encodeMilliseconds;
                TimelineBytes = timelineBytes;
                Backend = backend;
                WorkingSetBeforeBytes = workingSetBeforeBytes;
                WorkingSetAfterBytes = workingSetAfterBytes;
                WorkingSetPeakBytes = workingSetPeakBytes;
                WorkingSetPeakDeltaBytes = workingSetPeakDeltaBytes;
            }
        }

        private static long GetCurrentWorkingSetBytes()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                return process.WorkingSet64;
            }
            catch
            {
                return Environment.WorkingSet;
            }
        }

        private sealed class WorkingSetSampler : IDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private Task _task;
            public long PeakBytes { get; private set; }

            public void Start()
            {
                PeakBytes = GetCurrentWorkingSetBytes();
                _task = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        try
                        {
                            long current = GetCurrentWorkingSetBytes();
                            if (current > PeakBytes)
                                PeakBytes = current;
                            await Task.Delay(10, _stop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                });
            }

            public void Stop()
            {
                if (_task == null)
                    return;
                _stop.Cancel();
                try { _task.Wait(); } catch (AggregateException) { }
                _task = null;
            }

            public void Dispose()
            {
                Stop();
                _stop.Dispose();
            }
        }

        internal sealed class GifPreparationResult
        {
            public readonly bool Ok;
            public readonly Dictionary<string, string> ConvertedByOriginal;
            public readonly Dictionary<string, string> ErrorsByOriginal;
            public readonly Dictionary<string, GifPreparationMetrics> MetricsByOriginal;
            public readonly string Error;

            private GifPreparationResult(
                bool ok,
                Dictionary<string, string> convertedByOriginal,
                Dictionary<string, string> errorsByOriginal,
                Dictionary<string, GifPreparationMetrics> metricsByOriginal,
                string error
            )
            {
                Ok = ok;
                ConvertedByOriginal = convertedByOriginal;
                ErrorsByOriginal = errorsByOriginal;
                MetricsByOriginal = metricsByOriginal;
                Error = error;
            }

            public static GifPreparationResult Success(
                Dictionary<string, string> converted,
                Dictionary<string, string> errors,
                Dictionary<string, GifPreparationMetrics> metrics
            ) => new GifPreparationResult(true, converted, errors, metrics, null);

            public static GifPreparationResult Failure(string error) =>
                new GifPreparationResult(
                    false,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, GifPreparationMetrics>(StringComparer.OrdinalIgnoreCase),
                    error
                );
        }
    }
}
