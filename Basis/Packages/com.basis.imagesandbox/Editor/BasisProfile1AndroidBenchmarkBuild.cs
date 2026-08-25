using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Basis.ImagePickup;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.ImageSandbox.Editor
{
    public static class BasisProfile1AndroidBenchmarkBuild
    {
        private const string BenchmarkDefine = "BASIS_PROFILE1_ANDROID_BENCHMARK";
        private const string StreamingAssetRoot = "Assets/StreamingAssets/Profile1AndroidBenchmark";
        private const string TemporaryScenePath = "Assets/Profile1AndroidBenchmark.unity";
        private const string BenchmarkIdentifier = "com.basisvr.profile1benchmark";
        private const long MaximumLinearMemoryBytes = 256L * 1024L * 1024L;
        private const ulong PreparationFuel = 96_000_000_000UL;

        public static void Build()
        {
            string corpusRoot = Path.GetFullPath(RequireArgument("profile1CorpusPath"));
            string buildPath = Path.GetFullPath(RequireArgument("customBuildPath"));
            if (!Directory.Exists(corpusRoot))
                throw new BuildFailedException("Profile 1 codec corpus directory does not exist: " + corpusRoot);

            string jxlRoot = Path.Combine(corpusRoot, "jxl");
            string gifRoot = Path.Combine(corpusRoot, "gif-conformance");
            if (!Directory.Exists(jxlRoot) || !Directory.Exists(gifRoot))
            {
                throw new BuildFailedException(
                    "The codec corpus must contain both 'jxl' and 'gif-conformance' directories: " + corpusRoot
                );
            }

            TextAsset decoderAsset = Resources.Load<TextAsset>(BasisProfile1SandboxResources.DecoderResourcePath);
            if (decoderAsset == null || decoderAsset.bytes == null || decoderAsset.bytes.Length == 0)
            {
                throw new BuildFailedException(
                    "Profile 1 WASM decoder resource is missing. The CI workflow must build it before the Android benchmark player."
                );
            }

            NamedBuildTarget android = NamedBuildTarget.Android;
            string originalDefines = PlayerSettings.GetScriptingDefineSymbols(android);
            string originalIdentifier = PlayerSettings.GetApplicationIdentifier(android);
            string originalProductName = PlayerSettings.productName;

            try
            {
                EnsureAndroidTarget();
                Directory.CreateDirectory(Path.GetDirectoryName(buildPath) ?? ".");
                PreparePackagedCorpus(corpusRoot, decoderAsset.bytes);
                string packagedManifest = Path.GetFullPath(
                    Path.Combine(Application.dataPath, "StreamingAssets/Profile1AndroidBenchmark/manifest.json")
                );
                File.Copy(
                    packagedManifest,
                    Path.Combine(Path.GetDirectoryName(buildPath) ?? ".", "profile1-android-benchmark-manifest.json"),
                    true
                );
                CreateBenchmarkScene();
                PlayerSettings.SetScriptingDefineSymbols(android, AddDefine(originalDefines, BenchmarkDefine));
                PlayerSettings.SetApplicationIdentifier(android, BenchmarkIdentifier);
                PlayerSettings.productName = "Basis Profile1 Android Benchmark";
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { TemporaryScenePath },
                    locationPathName = buildPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.None,
                };
                Debug.Log(
                    $"[Profile1AndroidBenchmark] Building {buildPath} with {BenchmarkDefine}, "
                    + $"identifier={BenchmarkIdentifier}."
                );
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException(
                        $"Profile 1 Android benchmark player build failed: {report.summary.result}; "
                        + $"errors={report.summary.totalErrors}, warnings={report.summary.totalWarnings}."
                    );
                }
                Debug.Log(
                    $"[Profile1AndroidBenchmark] Build succeeded: {report.summary.outputPath} "
                    + $"({report.summary.totalSize:N0} bytes)."
                );
            }
            finally
            {
                PlayerSettings.SetScriptingDefineSymbols(android, originalDefines);
                PlayerSettings.SetApplicationIdentifier(android, originalIdentifier);
                PlayerSettings.productName = originalProductName;
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemporaryScenePath) != null)
                    AssetDatabase.DeleteAsset(TemporaryScenePath);
                if (AssetDatabase.IsValidFolder(StreamingAssetRoot))
                    AssetDatabase.DeleteAsset(StreamingAssetRoot);
                AssetDatabase.SaveAssets();
            }
        }

        private static void PreparePackagedCorpus(string corpusRoot, byte[] decoderBytes)
        {
            if (AssetDatabase.IsValidFolder(StreamingAssetRoot))
                AssetDatabase.DeleteAsset(StreamingAssetRoot);
            string absoluteRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "StreamingAssets/Profile1AndroidBenchmark"));
            string payloadRoot = Path.Combine(absoluteRoot, "payloads");
            Directory.CreateDirectory(payloadRoot);

            string[] jxlFiles = Directory.GetFiles(Path.Combine(corpusRoot, "jxl"), "*.jxl", SearchOption.AllDirectories);
            string[] gifFiles = Directory.GetFiles(Path.Combine(corpusRoot, "gif-conformance"), "*.gif", SearchOption.AllDirectories);
            Array.Sort(jxlFiles, StringComparer.OrdinalIgnoreCase);
            Array.Sort(gifFiles, StringComparer.OrdinalIgnoreCase);

            var manifest = new BasisProfile1AndroidBenchmarkManifest
            {
                basisCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "unknown",
                corpusCommit = Environment.GetEnvironmentVariable("PROFILE1_CODEC_CORPUS_COMMIT") ?? "unknown",
                generatedUtc = DateTime.UtcNow.ToString("O"),
                decoderWasmSha256 = ComputeSha256(decoderBytes),
                libjxlVersion = BasisProfile1SandboxDecoder.LibJxlVersion,
                libjxlCommit = BasisProfile1SandboxDecoder.LibJxlCommit,
                emscriptenVersion = BasisProfile1SandboxDecoder.EmscriptenVersion,
                wasmtimeVersion = BasisProfile1SandboxDecoder.WasmtimeVersion,
            };

            int payloadIndex = 0;
            var limits = new BasisProfile1SandboxLimits(
                MaximumLinearMemoryBytes,
                PreparationFuel,
                TimeSpan.FromSeconds(30)
            );
            using var decoder = new BasisProfile1SandboxDecoder((byte[])decoderBytes.Clone(), limits);

            foreach (string sourcePath in jxlFiles)
            {
                string relative = RelativeCorpusPath(corpusRoot, sourcePath);
                byte[] source = File.ReadAllBytes(sourcePath);

                var rawEntry = NewEntry(relative, "jxl", "raw-conformance", source.LongLength);
                if (BasisProfile1BenchmarkWindow.TryPrepareCanonicalProfile1(
                        source,
                        out BasisProfile1BenchmarkWindow.PreparedFixture rawPrepared,
                        out string rawError))
                {
                    rawEntry.preparationKind = "RawJxlConformance+" + rawPrepared.PreparationKind;
                    rawEntry.payloadPath = WritePayload(payloadRoot, ref payloadIndex, rawPrepared.Payload);
                }
                else
                {
                    rawEntry.preparationKind = "RawJxlConformance";
                    rawEntry.preparationError = rawError;
                }
                manifest.entries.Add(rawEntry);

                var localEntry = NewEntry(relative, "jxl", "local-import", source.LongLength);
                PrepareLocalJxl(decoder, source, localEntry, payloadRoot, ref payloadIndex);
                manifest.entries.Add(localEntry);
            }

            foreach (string sourcePath in gifFiles)
            {
                string relative = RelativeCorpusPath(corpusRoot, sourcePath);
                var entry = NewEntry(relative, "gif", "local-import", new FileInfo(sourcePath).Length);
                entry.preparationKind = "GifLosslessFullCanvas+CanonicalProfile1";
                if (BasisProfile1GifBenchmarkPreparation.TryConvertSynchronously(
                        sourcePath,
                        out byte[] profile1,
                        out string error))
                {
                    entry.payloadPath = WritePayload(payloadRoot, ref payloadIndex, profile1);
                }
                else
                {
                    entry.preparationError = error;
                }
                manifest.entries.Add(entry);
            }

            string manifestPath = Path.Combine(absoluteRoot, "manifest.json");
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            Debug.Log(
                $"[Profile1AndroidBenchmark] Packaged {manifest.entries.Count} entries: "
                + $"{jxlFiles.Length} raw JXL + {jxlFiles.Length} local JXL + {gifFiles.Length} GIF; "
                + $"payload files={payloadIndex}."
            );
            if (manifest.entries.Count != jxlFiles.Length * 2 + gifFiles.Length)
                throw new BuildFailedException("Profile 1 Android benchmark manifest entry count is inconsistent.");
            if (jxlFiles.Length != 184 || gifFiles.Length != 39)
            {
                Debug.LogWarning(
                    $"[Profile1AndroidBenchmark] Expected the current Imazen corpus shape of 184 JXL + 39 GIF, "
                    + $"found {jxlFiles.Length} JXL + {gifFiles.Length} GIF. The build will continue with the checked-out corpus revision."
                );
            }
        }

        private static void PrepareLocalJxl(
            BasisProfile1SandboxDecoder decoder,
            byte[] source,
            BasisProfile1AndroidBenchmarkManifestEntry entry,
            string payloadRoot,
            ref int payloadIndex)
        {
            try
            {
                byte[] profile1 = null;
                bool alreadyProfile1 = false;
                if (BasisProfile1BenchmarkWindow.TryPrepareCanonicalProfile1(
                        source,
                        out BasisProfile1BenchmarkWindow.PreparedFixture direct,
                        out _))
                {
                    BasisProfile1SandboxPreflight preflight = decoder.Preflight(direct.Payload);
                    if (preflight.Status == BasisProfile1SandboxStatus.Success)
                    {
                        alreadyProfile1 = true;
                        profile1 = direct.Payload;
                    }
                }

                if (!alreadyProfile1)
                {
                    if (!BasisProfile1EditorNative.TryDecodeJxlTimeline(source, out byte[] timeline, out string decodeError))
                    {
                        entry.preparationKind = "JxlTranscodedToProfile1";
                        entry.preparationError = decodeError;
                        return;
                    }
                    if (!BasisProfile1EditorNative.TryEncodeTimeline(timeline, out profile1, out string encodeError))
                    {
                        entry.preparationKind = "JxlTranscodedToProfile1";
                        entry.preparationError = encodeError;
                        return;
                    }
                }

                entry.preparationKind = alreadyProfile1 ? "JxlAlreadyProfile1" : "JxlTranscodedToProfile1";
                entry.payloadPath = WritePayload(payloadRoot, ref payloadIndex, profile1);
            }
            catch (Exception exception)
            {
                entry.preparationKind = "JxlTranscodedToProfile1";
                entry.preparationError = "JPEG XL local import failed: " + exception.Message;
            }
        }

        private static BasisProfile1AndroidBenchmarkManifestEntry NewEntry(
            string fixture,
            string sourceKind,
            string mode,
            long originalPayloadBytes) => new()
        {
            fixture = fixture + " [" + mode + "]",
            sourceKind = sourceKind,
            mode = mode,
            originalPayloadBytes = originalPayloadBytes,
        };

        private static string WritePayload(string payloadRoot, ref int payloadIndex, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                throw new InvalidOperationException("Prepared Profile 1 payload is empty.");
            string fileName = payloadIndex.ToString("D4") + ".jxl";
            payloadIndex++;
            File.WriteAllBytes(Path.Combine(payloadRoot, fileName), payload);
            return "payloads/" + fileName;
        }

        private static string RelativeCorpusPath(string corpusRoot, string path) =>
            Path.GetRelativePath(corpusRoot, path).Replace('\\', '/');

        private static void CreateBenchmarkScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemporaryScenePath) != null)
                AssetDatabase.DeleteAsset(TemporaryScenePath);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, TemporaryScenePath))
                throw new BuildFailedException("Could not create the temporary Profile 1 Android benchmark scene.");
        }

        private static void EnsureAndroidTarget()
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
                throw new BuildFailedException("Android Build Support is not installed in this Unity editor.");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android &&
                !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new BuildFailedException("Could not switch Unity to the Android build target.");
            }
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            return string.Concat(hash.Select(value => value.ToString("x2")));
        }

        private static string AddDefine(string symbols, string define)
        {
            var values = new HashSet<string>(
                (symbols ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal
            );
            values.Add(define);
            return string.Join(";", values.OrderBy(value => value, StringComparer.Ordinal));
        }

        private static string RequireArgument(string name)
        {
            string value = GetArgument(name);
            if (string.IsNullOrWhiteSpace(value))
                throw new BuildFailedException("Missing required command-line argument -" + name + ".");
            return value;
        }

        private static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            string key = "-" + name;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
