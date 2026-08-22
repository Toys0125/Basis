using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.ImageSandbox.Editor
{
    internal static class BasisProfile1EditorNativeBuildMenu
    {
        private const string MenuPath = "Basis/Debug/JPEG XL Profile 1/Build Editor Native Codec";
        private const string WindowsScript = "Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-editor-native.ps1";
        private const string UnixScript = "Packages/com.basis.imagesandbox/Native~/Profile1/build-profile1-editor-native.sh";

        private static readonly object OutputLock = new object();
        private static readonly StringBuilder Output = new StringBuilder();
        private static Process _process;

        [InitializeOnLoadMethod]
        private static void InstallPendingWindowsPlugin()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return;

            string pending = GetPendingPluginPath();
            string installed = GetPluginFilePath();
            if (string.IsNullOrEmpty(pending) || string.IsNullOrEmpty(installed) || !File.Exists(pending))
                return;

            try
            {
                File.Copy(pending, installed, true);
                File.Delete(pending);
                Debug.Log("Installed the pending Profile 1 editor-native codec from the previous Unity session.");
                EditorApplication.delayCall += () => AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch (IOException exception)
            {
                Debug.LogWarning(
                    "Could not install the pending Profile 1 editor-native codec. It will be retried on the next Unity launch.\n"
                        + exception.Message
                );
            }
        }

        [MenuItem(MenuPath, false, 609)]
        private static void Build()
        {
            if (_process != null)
                return;

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            bool windows = Application.platform == RuntimePlatform.WindowsEditor;
            string script = Path.Combine(projectRoot, windows ? WindowsScript : UnixScript);
            if (!File.Exists(script))
            {
                EditorUtility.DisplayDialog("JPEG XL Profile 1", "Editor-native build script was not found:\n" + script, "OK");
                return;
            }

            lock (OutputLock)
                Output.Clear();

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = windows ? "powershell.exe" : "bash",
                    Arguments = windows
                        ? $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script)}"
                        : Quote(script.Replace('\\', '/')),
                    WorkingDirectory = projectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += AppendOutput;
                process.ErrorDataReceived += AppendOutput;
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("Failed to start the editor-native build process.");
                }
                _process = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                EditorApplication.update += Poll;
                Debug.Log("Building pinned JPEG XL Profile 1 editor-native codec...");
            }
            catch (Exception exception)
            {
                _process?.Dispose();
                _process = null;
                EditorUtility.DisplayDialog("JPEG XL Profile 1", "Could not start the editor-native build.\n\n" + exception.Message, "OK");
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateBuild() => _process == null;

        private static void AppendOutput(object sender, DataReceivedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.Data))
                return;
            lock (OutputLock)
                Output.AppendLine(args.Data);
        }

        private static void Poll()
        {
            Process process = _process;
            if (process == null || !process.HasExited)
                return;

            EditorApplication.update -= Poll;
            process.WaitForExit();
            int exitCode = process.ExitCode;
            string output;
            lock (OutputLock)
                output = Output.ToString();
            _process = null;
            process.Dispose();

            if (exitCode != 0)
            {
                Debug.LogError("Profile 1 editor-native codec build failed.\n" + output);
                EditorUtility.DisplayDialog("JPEG XL Profile 1", "Editor-native codec build failed. Check the Console.", "OK");
                return;
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            string assetPath = GetPluginAssetPath();
            bool restartRequired = Application.platform == RuntimePlatform.WindowsEditor && File.Exists(GetPendingPluginPath());
            if (assetPath == null || !(AssetImporter.GetAtPath(assetPath) is PluginImporter importer))
            {
                Debug.LogError("Profile 1 editor-native codec built, but Unity did not import the plugin.\n" + output);
                EditorUtility.DisplayDialog("JPEG XL Profile 1", "Native codec built, but Unity could not import it. Check the Console.", "OK");
                return;
            }

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(true);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false);
            importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false);
            importer.SetCompatibleWithPlatform(BuildTarget.Android, false);
            importer.SetCompatibleWithPlatform(BuildTarget.iOS, false);
            SetEditorPlatform(importer);
            importer.SaveAndReimport();

            if (restartRequired)
            {
                Debug.Log(
                    "Profile 1 editor-native codec rebuilt successfully. The previous Windows DLL is loaded, so the replacement is staged for the next Unity launch.\n"
                        + output
                );
                EditorUtility.DisplayDialog(
                    "JPEG XL Profile 1",
                    "The native codec rebuilt successfully, but Windows has the previous DLL loaded. The replacement is staged and will be installed automatically the next time Unity starts.\n\nRestart Unity before rerunning the GIF benchmark.",
                    "OK"
                );
                return;
            }

            Debug.Log("Profile 1 editor-native codec is ready and restricted to the Unity Editor.\n" + output);
            EditorUtility.DisplayDialog(
                "JPEG XL Profile 1",
                "The pinned native libjxl codec was built and imported for the Unity Editor only. Player builds continue to use the Profile 1 WASM/Wasmtime path.",
                "OK"
            );
        }

        internal static string GetPendingPluginPath()
        {
            string assetPath = GetPluginAssetPath();
            if (assetPath == null)
                return null;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath + ".pending"));
        }

        internal static string GetPluginFilePath()
        {
            string assetPath = GetPluginAssetPath();
            if (assetPath == null)
                return null;
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        private static string GetPluginAssetPath()
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
                return "Packages/com.basis.imagesandbox/Plugins/Editor/Windows/x86_64/basis_profile1_editor.dll";
            if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x86_64";
                return $"Packages/com.basis.imagesandbox/Plugins/Editor/Linux/{arch}/libbasis_profile1_editor.so";
            }
            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";
                return $"Packages/com.basis.imagesandbox/Plugins/Editor/macOS/{arch}/libbasis_profile1_editor.dylib";
            }
            return null;
        }

        private static void SetEditorPlatform(PluginImporter importer)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                importer.SetEditorData("OS", "Windows");
                importer.SetEditorData("CPU", "x86_64");
            }
            else if (Application.platform == RuntimePlatform.LinuxEditor)
            {
                importer.SetEditorData("OS", "Linux");
                importer.SetEditorData("CPU", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x86_64");
            }
            else if (Application.platform == RuntimePlatform.OSXEditor)
            {
                importer.SetEditorData("OS", "OSX");
                importer.SetEditorData("CPU", RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x86_64");
            }
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
