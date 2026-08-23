using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1ExternalAnimationPreparation
    {
        internal readonly struct DecodeResult
        {
            public readonly bool Ok;
            public readonly byte[] Timeline;
            public readonly string Error;
            public readonly string Backend;
            public readonly double DecodeMilliseconds;

            public DecodeResult(bool ok, byte[] timeline, string error, string backend, double decodeMilliseconds)
            {
                Ok = ok;
                Timeline = timeline;
                Error = error;
                Backend = backend;
                DecodeMilliseconds = decodeMilliseconds;
            }
        }

        private readonly struct AnimationMetadata
        {
            public readonly uint Width;
            public readonly uint Height;
            public readonly uint Loops;
            public readonly uint[] DurationsMicroseconds;

            public AnimationMetadata(uint width, uint height, uint loops, uint[] durationsMicroseconds)
            {
                Width = width;
                Height = height;
                Loops = loops;
                DurationsMicroseconds = durationsMicroseconds;
            }
        }

        public static bool IsApngFile(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                return TryParseApng(bytes, out _, requireAnimation: true, out _);
            }
            catch
            {
                return false;
            }
        }

        public static Task<DecodeResult> DecodeAsync(
            string sourcePath,
            string ffmpegPath,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => Decode(sourcePath, ffmpegPath, cancellationToken), cancellationToken);
        }

        private static DecodeResult Decode(
            string sourcePath,
            string ffmpegPath,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                byte[] source = File.ReadAllBytes(sourcePath);
                string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                AnimationMetadata metadata;
                string metadataError;
                if (extension == ".webp")
                {
                    if (!TryParseWebP(source, out metadata, out metadataError))
                        return Failure(metadataError, stopwatch);
                }
                else if (extension == ".apng" || extension == ".png")
                {
                    if (!TryParseApng(source, out metadata, requireAnimation: extension == ".apng", out metadataError))
                        return Failure(metadataError, stopwatch);
                }
                else
                {
                    return Failure("Unsupported external animation source extension: " + extension, stopwatch);
                }

                if (metadata.Width == 0 || metadata.Height == 0 || metadata.DurationsMicroseconds.Length == 0)
                    return Failure("Animation metadata did not contain a decodable canvas and frame timeline.", stopwatch);

                byte[] rgba = DecodeWithFfmpeg(sourcePath, ffmpegPath, cancellationToken, out string ffmpegError);
                if (rgba == null)
                    return Failure(ffmpegError, stopwatch);

                ulong frameBytes = (ulong)metadata.Width * metadata.Height * 4UL;
                ulong expectedBytes = frameBytes * (ulong)metadata.DurationsMicroseconds.Length;
                if (expectedBytes > int.MaxValue || rgba.LongLength != (long)expectedBytes)
                {
                    return Failure(
                        $"FFmpeg returned {rgba.LongLength} RGBA bytes, expected {expectedBytes} for "
                        + $"{metadata.DurationsMicroseconds.Length} frame(s) at {metadata.Width}x{metadata.Height}.",
                        stopwatch
                    );
                }

                byte[] timeline = BuildTimeline(metadata, rgba);
                stopwatch.Stop();
                return new DecodeResult(true, timeline, null, "FFmpeg local RGBA decode", stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Failure("Local animation decode failed: " + exception.Message, stopwatch);
            }
        }

        private static DecodeResult Failure(string error, Stopwatch stopwatch)
        {
            stopwatch.Stop();
            return new DecodeResult(false, null, error, "FFmpeg local RGBA decode", stopwatch.Elapsed.TotalMilliseconds);
        }

        private static byte[] DecodeWithFfmpeg(
            string sourcePath,
            string ffmpegPath,
            CancellationToken cancellationToken,
            out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                error = "FFmpeg path is empty. Set it in the benchmark window to test APNG/WebP local import.";
                return null;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -loglevel error -i " + Quote(sourcePath)
                    + " -map 0:v:0 -vsync 0 -f rawvideo -pix_fmt rgba pipe:1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            try
            {
                using var process = new Process { StartInfo = startInfo };
                if (!process.Start())
                {
                    error = "FFmpeg did not start.";
                    return null;
                }
                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill();
                    }
                    catch
                    {
                    }
                });

                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                using var output = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(output);
                process.WaitForExit();
                string stderr = stderrTask.GetAwaiter().GetResult();
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    error = "FFmpeg decode failed with exit code " + process.ExitCode + ": " + stderr.Trim();
                    return null;
                }
                return output.ToArray();
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                error = "FFmpeg could not be launched from '" + ffmpegPath + "': " + exception.Message;
                return null;
            }
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static byte[] BuildTimeline(AnimationMetadata metadata, byte[] rgba)
        {
            using var stream = new MemoryStream(checked(24 + metadata.DurationsMicroseconds.Length * 4 + rgba.Length));
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(new byte[] { (byte)'B', (byte)'P', (byte)'1', (byte)'G', (byte)'I', (byte)'F', (byte)'0', (byte)'1' });
            writer.Write(metadata.Width);
            writer.Write(metadata.Height);
            writer.Write((uint)metadata.DurationsMicroseconds.Length);
            writer.Write(metadata.Loops);
            foreach (uint duration in metadata.DurationsMicroseconds)
                writer.Write(duration);
            writer.Write(rgba);
            writer.Flush();
            return stream.ToArray();
        }

        private static bool TryParseApng(
            byte[] bytes,
            out AnimationMetadata metadata,
            bool requireAnimation,
            out string error)
        {
            metadata = default;
            error = null;
            byte[] signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
            if (bytes == null || bytes.Length < signature.Length)
            {
                error = "PNG/APNG source is truncated.";
                return false;
            }
            for (int i = 0; i < signature.Length; i++)
            {
                if (bytes[i] != signature[i])
                {
                    error = "PNG/APNG signature is invalid.";
                    return false;
                }
            }

            uint width = 0;
            uint height = 0;
            uint declaredFrames = 0;
            uint loops = 0;
            bool sawAnimation = false;
            var durations = new List<uint>();
            int offset = 8;
            while (offset <= bytes.Length - 12)
            {
                uint length = ReadBe32(bytes, offset);
                if (length > int.MaxValue || offset + 12L + length > bytes.Length)
                {
                    error = "PNG/APNG chunk length is invalid.";
                    return false;
                }
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                int data = offset + 8;
                if (type == "IHDR" && length >= 8)
                {
                    width = ReadBe32(bytes, data);
                    height = ReadBe32(bytes, data + 4);
                }
                else if (type == "acTL" && length == 8)
                {
                    declaredFrames = ReadBe32(bytes, data);
                    loops = ReadBe32(bytes, data + 4);
                    sawAnimation = true;
                }
                else if (type == "fcTL" && length == 26)
                {
                    uint numerator = ReadBe16(bytes, data + 20);
                    uint denominator = ReadBe16(bytes, data + 22);
                    if (denominator == 0)
                        denominator = 100;
                    ulong scaled = (ulong)numerator * 1_000_000UL;
                    if (scaled == 0 || scaled % denominator != 0 || scaled / denominator > uint.MaxValue)
                    {
                        error = "APNG frame timing cannot be represented exactly in Profile 1 microseconds.";
                        return false;
                    }
                    durations.Add((uint)(scaled / denominator));
                }
                offset = checked(offset + 12 + (int)length);
                if (type == "IEND")
                    break;
            }

            if (!sawAnimation)
            {
                if (requireAnimation)
                {
                    error = "The .apng source has no acTL animation chunk.";
                    return false;
                }
                durations.Add(33_334);
                declaredFrames = 1;
                loops = 0;
            }
            if (width == 0 || height == 0 || declaredFrames == 0 || durations.Count != declaredFrames)
            {
                error = $"APNG metadata is inconsistent: declared {declaredFrames} frame(s), found {durations.Count} frame-control chunk(s).";
                return false;
            }
            metadata = new AnimationMetadata(width, height, loops, durations.ToArray());
            return true;
        }

        private static bool TryParseWebP(byte[] bytes, out AnimationMetadata metadata, out string error)
        {
            metadata = default;
            error = null;
            if (bytes == null || bytes.Length < 12 ||
                Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WEBP")
            {
                error = "WebP RIFF signature is invalid.";
                return false;
            }

            uint width = 0;
            uint height = 0;
            uint loops = 0;
            bool sawAnimation = false;
            var durations = new List<uint>();
            int offset = 12;
            while (offset <= bytes.Length - 8)
            {
                string type = Encoding.ASCII.GetString(bytes, offset, 4);
                uint length = ReadLe32(bytes, offset + 4);
                long end = offset + 8L + length;
                if (length > int.MaxValue || end > bytes.Length)
                {
                    error = "WebP chunk length is invalid.";
                    return false;
                }
                int data = offset + 8;
                if (type == "VP8X" && length >= 10)
                {
                    width = ReadLe24(bytes, data + 4) + 1;
                    height = ReadLe24(bytes, data + 7) + 1;
                }
                else if (type == "ANIM" && length >= 6)
                {
                    loops = ReadLe16(bytes, data + 4);
                    sawAnimation = true;
                }
                else if (type == "ANMF" && length >= 16)
                {
                    uint durationMs = ReadLe24(bytes, data + 12);
                    ulong durationUs = (ulong)durationMs * 1000UL;
                    if (durationUs == 0 || durationUs > uint.MaxValue)
                    {
                        error = "WebP frame timing cannot be represented by Profile 1.";
                        return false;
                    }
                    durations.Add((uint)durationUs);
                }
                offset = checked((int)(end + (length & 1U)));
            }

            if (!sawAnimation || durations.Count == 0)
            {
                error = "WebP source is not an animated WebP (ANIM/ANMF chunks were not found).";
                return false;
            }
            if (width == 0 || height == 0)
            {
                error = "Animated WebP is missing a valid VP8X canvas.";
                return false;
            }
            metadata = new AnimationMetadata(width, height, loops, durations.ToArray());
            return true;
        }

        private static ushort ReadBe16(byte[] bytes, int offset) =>
            (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

        private static uint ReadBe32(byte[] bytes, int offset) =>
            ((uint)bytes[offset] << 24)
            | ((uint)bytes[offset + 1] << 16)
            | ((uint)bytes[offset + 2] << 8)
            | bytes[offset + 3];

        private static ushort ReadLe16(byte[] bytes, int offset) =>
            (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

        private static uint ReadLe24(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16));

        private static uint ReadLe32(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
