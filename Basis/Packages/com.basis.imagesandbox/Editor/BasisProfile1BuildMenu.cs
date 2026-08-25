using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1BuildMenu
    {
        private const string MenuPath = "Basis/Debug/JPEG XL Profile 1/Build Test Decoder";
        private const string BashBuildScriptRelativePath =
            "Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-wasm.sh";
        private const string PowerShellBuildScriptRelativePath =
            "Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-wasm.ps1";

        private static readonly object OutputLock = new object();
        private static readonly StringBuilder Output = new StringBuilder();
        private static Process _buildProcess;

        [MenuItem(MenuPath, false, 608)]
        private static void BuildTestDecoder()
        {
            if (_buildProcess != null)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            bool isWindows = Application.platform == RuntimePlatform.WindowsEditor;
            string scriptPath = Path.Combine(
                projectRoot,
                isWindows ? PowerShellBuildScriptRelativePath : BashBuildScriptRelativePath
            );
            if (!File.Exists(scriptPath))
            {
                ShowFailure($"Profile 1 build script was not found:\n{scriptPath}");
                return;
            }

            lock (OutputLock)
                Output.Clear();

            try
            {
                string normalizedScriptPath = scriptPath.Replace('\\', '/');
                var startInfo = new ProcessStartInfo
                {
                    FileName = isWindows ? "powershell.exe" : "bash",
                    Arguments = isWindows
                        ? $"-NoProfile -ExecutionPolicy Bypass -File {Quote(scriptPath)}"
                        : Quote(normalizedScriptPath),
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var process = new Process
                {
                    StartInfo = startInfo,
                };
                process.OutputDataReceived += AppendOutput;
                process.ErrorDataReceived += AppendOutput;

                if (!process.Start())
                {
                    process.Dispose();
                    ShowFailure("Failed to start the Profile 1 decoder build process.");
                    return;
                }

                _buildProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                EditorApplication.update += PollBuildProcess;
                Debug.Log("Building pinned JPEG XL Profile 1 WASM test decoder...");
            }
            catch (Exception exception)
            {
                _buildProcess?.Dispose();
                _buildProcess = null;
                ShowFailure(
                    "Could not start the Profile 1 decoder build. Docker is required; Windows uses PowerShell and other editor platforms use bash.\n\n"
                    + exception.Message
                );
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateBuildTestDecoder() => _buildProcess == null;

        private static void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data))
                return;
            lock (OutputLock)
                Output.AppendLine(args.Data);
        }

        private static void PollBuildProcess()
        {
            Process process = _buildProcess;
            if (process == null || !process.HasExited)
                return;

            EditorApplication.update -= PollBuildProcess;
            process.WaitForExit();
            int exitCode = process.ExitCode;

            string output;
            lock (OutputLock)
                output = Output.ToString();

            FinishBuild(process, exitCode, output);
        }

        private static void FinishBuild(Process process, int exitCode, string output)
        {
            if (ReferenceEquals(_buildProcess, process))
                _buildProcess = null;
            process.Dispose();

            if (exitCode != 0)
            {
                Debug.LogError("JPEG XL Profile 1 decoder build failed.\n" + output);
                ShowFailure(
                    "Profile 1 decoder build failed. Check the Console for the complete build output."
                );
                return;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            TextAsset decoder = Resources.Load<TextAsset>(BasisProfile1SandboxResources.DecoderResourcePath);
            if (decoder == null || decoder.bytes == null || decoder.bytes.Length == 0)
            {
                Debug.LogError(
                    "JPEG XL Profile 1 decoder built successfully but Unity could not load the generated Resources asset.\n"
                    + output
                );
                ShowFailure(
                    "The decoder was built, but Unity could not load it from Resources. Check the Console."
                );
                return;
            }

            if (!TryRunDecoderSmokeTest(out string smokeError))
            {
                Debug.LogError(
                    "JPEG XL Profile 1 decoder built and imported, but the runtime smoke test failed.\n"
                    + smokeError
                    + "\n"
                    + output
                );
                ShowFailure(
                    "The decoder was built and imported, but the runtime smoke test failed. Check the Console.\n\n"
                    + smokeError
                );
                return;
            }

            Debug.Log(
                $"JPEG XL Profile 1 test decoder is ready ({decoder.bytes.Length:N0} bytes). Windows/native Wasmtime smoke test passed.\n{output}"
            );
            EditorUtility.DisplayDialog(
                "JPEG XL Profile 1",
                "The pinned WASM decoder was built, SHA-256 verified, imported, loaded, and executed successfully.\n\n"
                    + "The smoke test verified the real two-frame JPEG XL fixture, frame timing, and exact RGBA8 output.\n\n"
                    + "You can now run Basis.ImagePickup.Tests.BasisJpegXlProfile1Tests in the Test Runner.",
                "OK"
            );
        }

        private static bool TryRunDecoderSmokeTest(out string error)
        {
            error = null;
            const string fixtureHex =
                "0000000c4a584c200d0a870a00000014667479706a786c20000000006a786c20"
                + "0000003d6a786c7000000000ff0a0070c17f841e008035010800b08d20000000"
                + "0068004b12a5428524d6f0b802001c00bf800800960e93120d16dd0701000000"
                + "2d6a786c7080000001080070d4300040000058004b12a5428524d233c62d6c00"
                + "00e00b8800b0c2e97d00";
            byte[] fixture = HexToBytes(fixtureHex);
            var limits = new BasisProfile1SandboxLimits(
                64L * 1024L * 1024L,
                1_000_000_000UL,
                TimeSpan.FromSeconds(30)
            );

            if (!BasisProfile1SandboxResources.TryCreateDecoder(limits, out BasisProfile1SandboxDecoder decoder, out error))
                return false;

            using (decoder)
            {
                BasisProfile1SandboxPreflight preflight = decoder.Preflight(fixture);
                if (
                    preflight.Status != BasisProfile1SandboxStatus.Success
                    || preflight.Width != 2
                    || preflight.Height != 1
                    || preflight.LogicalFrameCount != 2
                    || preflight.TotalPlayCount != 0
                    || preflight.SubmittedCanvasPixels != 4
                    || preflight.BaseTimelineMicroseconds != 83_335
                    || preflight.PublicRegularLayerCount != 2
                    || preflight.PublicRegularLayerPixels != 4
                    || preflight.CroppedLayerCount != 0
                    || preflight.ReferenceReadEdges != 0
                    || preflight.SavedReferenceCount != 0
                    || preflight.BlendOperationCount != 0
                    || preflight.MaximumReferenceChainDepth != 1
                    || preflight.PreviewPixels != 0
                    || preflight.CroppedLayerPixels != 0
                    || preflight.ReferenceReadPixels != 0
                    || preflight.SavedReferencePixels != 0
                    || preflight.BlendOperationPixels != 0
                    || preflight.ReferenceChainExtraPixels != 0
                    || preflight.DecodeWorkCandidate != 4_140
                )
                {
                    error = $"Unexpected Profile 1 preflight result: {preflight.Status}, {preflight.Width}x{preflight.Height}, {preflight.LogicalFrameCount} frames.";
                    return false;
                }

                byte[][] expectedFrames =
                {
                    HexToBytes("1127c900010203ff"),
                    HexToBytes("04050680070809ff"),
                };
                ulong[] expectedDurations = { 33_334UL, 50_001UL };
                int consumedFrames = 0;
                BasisProfile1SandboxStatus status = decoder.DecodeFrames(
                    fixture,
                    preflight,
                    (frameIndex, rgba, duration) =>
                    {
                        if (
                            frameIndex != consumedFrames
                            || frameIndex >= expectedFrames.Length
                            || duration != expectedDurations[frameIndex]
                            || !BytesEqual(rgba, expectedFrames[frameIndex])
                        )
                        {
                            return false;
                        }
                        consumedFrames++;
                        return true;
                    }
                );
                if (status != BasisProfile1SandboxStatus.Success || consumedFrames != 2)
                {
                    error = $"Profile 1 full decode smoke test failed with {status} after {consumedFrames} frames.";
                    return false;
                }
            }

            return true;
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static void ShowFailure(string message)
        {
            EditorUtility.DisplayDialog("JPEG XL Profile 1", message, "OK");
        }
    }
}
