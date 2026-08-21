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

            Debug.Log(
                $"JPEG XL Profile 1 test decoder is ready ({decoder.bytes.Length:N0} bytes).\n{output}"
            );
            EditorUtility.DisplayDialog(
                "JPEG XL Profile 1",
                "The pinned WASM decoder was built, SHA-256 verified, imported, and loaded successfully.\n\n"
                    + "You can now run Basis.ImagePickup.Tests.BasisJpegXlProfile1Tests in the Test Runner.",
                "OK"
            );
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static void ShowFailure(string message)
        {
            EditorUtility.DisplayDialog("JPEG XL Profile 1", message, "OK");
        }
    }
}
