using System;
using System.Collections.Generic;
using System.IO;
using Basis.ImagePickup;
using UnityEngine;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1GifBenchmarkPreparation
    {
        public static bool TryConvert(
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
                    string jxlPath = Path.Combine(jxlRoot, stem + ".jxl");
                    if (!TryBuildTimeline(gifPath, out byte[] timeline, out error))
                        return false;
                    File.WriteAllBytes(rawPath, timeline);
                    if (!BasisProfile1EditorNative.TryEncodeTimeline(timeline, out byte[] profile1, out error))
                    {
                        error = "GIF Profile 1 encode failed for " + gifPath + ": " + error;
                        return false;
                    }
                    File.WriteAllBytes(jxlPath, profile1);
                    convertedByOriginal[gifPath] = jxlPath;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "GIF benchmark preparation failed: " + exception.Message;
                return false;
            }
        }

        private static bool TryBuildTimeline(string gifPath, out byte[] timeline, out string error)
        {
            timeline = null;
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

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }
    }
}
