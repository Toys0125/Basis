using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Basis.ImagePickup;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1GifBenchmarkPreparation
    {
        private const string WindowsScript = "Packages/com.basis.imagesandbox/Native~/Profile1/encode-profile1-benchmark-gifs.ps1";
        private const string UnixScript = "Packages/com.basis.imagesandbox/Native~/Profile1/encode-profile1-benchmark-gifs.sh";

        public static bool TryConvert(
            string projectRoot,
            string[] gifPaths,
            string outputRoot,
            out Dictionary<string, string> convertedByOriginal,
            out string error
        )
        {
            convertedByOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            if (gifPaths == null || gifPaths.Length == 0)
                return true;

            string runName = "gif-prepared-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string runRoot = Path.Combine(outputRoot, runName);
            string rawRoot = Path.Combine(runRoot, "raw");
            string jxlRoot = Path.Combine(runRoot, "jxl");
            Directory.CreateDirectory(rawRoot);
            Directory.CreateDirectory(jxlRoot);

            try
            {
                for (int i = 0; i < gifPaths.Length; i++)
                {
                    string gifPath = gifPaths[i];
                    string stem = i.ToString("D6") + "_" + SanitizeFileName(Path.GetFileNameWithoutExtension(gifPath));
                    string rawPath = Path.Combine(rawRoot, stem + ".bp1gif");
                    if (!TryWriteTimeline(gifPath, rawPath, out error))
                        return false;
                    convertedByOriginal[gifPath] = Path.Combine(jxlRoot, stem + ".jxl");
                }

                bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;
                string scriptPath = Path.Combine(projectRoot, isWindows ? WindowsScript : UnixScript);
                if (!File.Exists(scriptPath))
                {
                    error = "GIF benchmark encoder script was not found: " + scriptPath;
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "powershell.exe" : "bash",
                    Arguments = isWindows
                        ? $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptPath)} -InputDirectory {Quote(rawRoot)} -OutputDirectory {Quote(jxlRoot)}"
                        : $"{Quote(scriptPath.Replace('\\', '/'))} {Quote(rawRoot.Replace('\\', '/'))} {Quote(jxlRoot.Replace('\\', '/'))}",
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    error = "Failed to start the trusted-local GIF benchmark encoder.";
                    return false;
                }
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(stdout))
                    Debug.Log(stdout);
                if (process.ExitCode != 0)
                {
                    error = "GIF benchmark encoder failed.\n" + stderr;
                    return false;
                }

                foreach (KeyValuePair<string, string> pair in convertedByOriginal)
                {
                    if (!File.Exists(pair.Value) || new FileInfo(pair.Value).Length == 0)
                    {
                        error = "GIF benchmark encoder did not produce output for " + pair.Key;
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "GIF benchmark preparation failed: " + exception.Message;
                return false;
            }
        }

        private static bool TryWriteTimeline(string gifPath, string rawPath, out string error)
        {
            error = null;
            byte[] source = File.ReadAllBytes(gifPath);
            using var request = BasisBurstGifDecoder.Schedule(source);
            using BasisBurstGifDecodeResult result = request.Complete();
            if (result == null || !result.Ok || result.Animation == null)
            {
                error = "GIF decode failed for " + gifPath + ": " + (result?.Error ?? "unknown error");
                return false;
            }

            BasisAnimatedImageData animation = result.Animation;
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

            using var stream = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None);
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

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
