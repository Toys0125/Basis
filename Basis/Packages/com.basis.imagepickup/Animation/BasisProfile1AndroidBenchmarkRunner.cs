using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Basis.ImageSandbox;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace Basis.ImagePickup
{
    [Serializable]
    internal sealed class BasisProfile1AndroidBenchmarkManifest
    {
        public int schemaVersion = 1;
        public string basisCommit;
        public string corpusCommit;
        public string generatedUtc;
        public string decoderWasmSha256;
        public string libjxlVersion;
        public string libjxlCommit;
        public string emscriptenVersion;
        public string wasmtimeVersion;
        public List<BasisProfile1AndroidBenchmarkManifestEntry> entries = new();
    }

    [Serializable]
    internal sealed class BasisProfile1AndroidBenchmarkManifestEntry
    {
        public string fixture;
        public string sourceKind;
        public string mode;
        public long originalPayloadBytes;
        public string preparationKind;
        public string preparationError;
        public string payloadPath;
    }

#if UNITY_ANDROID && BASIS_PROFILE1_ANDROID_BENCHMARK
    internal sealed class BasisProfile1AndroidBenchmarkRunner : MonoBehaviour
    {
        private const string Marker = "PROFILE1_ANDROID_BENCHMARK";
        private const string StreamingRoot = "Profile1AndroidBenchmark";
        private const string ManifestName = "manifest.json";
        private const long MaximumLinearMemoryBytes = 256L * 1024L * 1024L;
        private const ulong FuelLimit = 96_000_000_000UL;
        private static readonly TimeSpan FixtureTimeout = TimeSpan.FromSeconds(30);

        [Serializable]
        private sealed class ResultRow
        {
            public string fixture;
            public string sourceKind;
            public string mode;
            public long originalPayloadBytes;
            public int payloadBytes;
            public string preparationKind;
            public string preparationError;
            public string loadError;
            public double stageAMilliseconds;
            public string stageAStatus;
            public string stageAError;
            public double stageBMilliseconds;
            public string stageBStatus;
            public string stageBDiagnosticReason;
            public ulong stageBFuelConsumed;
            public ulong stageBLogicalHeaderFuelConsumed;
            public ulong stageBStructuralHeaderFuelConsumed;
            public ulong stageBValidationFuelConsumed;
            public double stageBLogicalHeaderMilliseconds;
            public double stageBStructuralHeaderMilliseconds;
            public double stageBValidationMilliseconds;
            public ulong stageBInitialMemoryBytes;
            public ulong stageBPeakMemoryBytes;
            public ulong stageBFinalMemoryBytes;
            public int stageBMemoryGrowthCount;
            public uint width;
            public uint height;
            public uint logicalFrames;
            public uint totalPlayCount;
            public ulong submittedCanvasPixels;
            public ulong baseTimelineMicroseconds;
            public ulong publicRegularLayerCount;
            public ulong publicRegularLayerPixels;
            public ulong croppedLayerCount;
            public ulong croppedLayerPixels;
            public ulong referenceReadEdges;
            public ulong referenceReadPixels;
            public ulong savedReferenceCount;
            public ulong savedReferencePixels;
            public ulong blendOperationCount;
            public ulong blendOperationPixels;
            public ulong maximumReferenceChainDepth;
            public ulong referenceChainExtraPixels;
            public ulong previewPixels;
            public ulong decodeWorkCandidate;
            public double decodeMilliseconds;
            public string decodeStatus;
            public ulong decodeFuelConsumed;
            public ulong decodeInitialMemoryBytes;
            public ulong decodePeakMemoryBytes;
            public ulong decodeFinalMemoryBytes;
            public int decodeMemoryGrowthCount;
            public int decodedFrames;
            public string decodeChecksum;
        }

        [Serializable]
        private sealed class Summary
        {
            public int schemaVersion = 1;
            public string basisCommit;
            public string corpusCommit;
            public string decoderWasmSha256;
            public string libjxlVersion;
            public string libjxlCommit;
            public string emscriptenVersion;
            public string wasmtimeVersion;
            public string deviceModel;
            public string deviceName;
            public string operatingSystem;
            public string processorType;
            public int processorCount;
            public int systemMemoryMiB;
            public string graphicsDeviceName;
            public string graphicsDeviceType;
            public string unityVersion;
            public string applicationIdentifier;
            public string startedUtc;
            public string completedUtc;
            public double moduleInitializationMilliseconds;
            public int totalEntries;
            public int preparationFailures;
            public int loadFailures;
            public int stageAFailures;
            public int stageBSuccesses;
            public int stageBFailures;
            public int decodeSuccesses;
            public int decodeFailures;
            public string csvPath;
            public string jsonlPath;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var gameObject = new GameObject("Profile1 Android Benchmark");
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            gameObject.AddComponent<BasisProfile1AndroidBenchmarkRunner>();
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return RunBenchmark();
        }

        private IEnumerator RunBenchmark()
        {
            string manifestUri = CombineStreamingUri(ManifestName);
            byte[] manifestBytes = null;
            string manifestError = null;
            yield return LoadBytes(manifestUri, bytes => manifestBytes = bytes, error => manifestError = error);
            if (manifestBytes == null)
            {
                Debug.LogError($"{Marker}_FAILED manifest={manifestUri} error={manifestError}");
                yield break;
            }

            BasisProfile1AndroidBenchmarkManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<BasisProfile1AndroidBenchmarkManifest>(Encoding.UTF8.GetString(manifestBytes));
            }
            catch (Exception exception)
            {
                Debug.LogError($"{Marker}_FAILED manifest_parse={exception.Message}");
                yield break;
            }
            if (manifest?.entries == null || manifest.entries.Count == 0)
            {
                Debug.LogError($"{Marker}_FAILED manifest contains no entries");
                yield break;
            }

            string outputDirectory = Path.Combine(Application.persistentDataPath, "Profile1AndroidBenchmark");
            Directory.CreateDirectory(outputDirectory);
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string csvPath = Path.Combine(outputDirectory, $"profile1-android-benchmark-{timestamp}.csv");
            string jsonlPath = Path.Combine(outputDirectory, $"profile1-android-benchmark-{timestamp}.jsonl");
            string summaryPath = Path.Combine(outputDirectory, $"profile1-android-benchmark-{timestamp}-summary.json");

            var summary = new Summary
            {
                basisCommit = manifest.basisCommit,
                corpusCommit = manifest.corpusCommit,
                decoderWasmSha256 = manifest.decoderWasmSha256,
                libjxlVersion = manifest.libjxlVersion,
                libjxlCommit = manifest.libjxlCommit,
                emscriptenVersion = manifest.emscriptenVersion,
                wasmtimeVersion = manifest.wasmtimeVersion,
                deviceModel = SystemInfo.deviceModel,
                deviceName = SystemInfo.deviceName,
                operatingSystem = SystemInfo.operatingSystem,
                processorType = SystemInfo.processorType,
                processorCount = SystemInfo.processorCount,
                systemMemoryMiB = SystemInfo.systemMemorySize,
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                unityVersion = Application.unityVersion,
                applicationIdentifier = Application.identifier,
                startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                totalEntries = manifest.entries.Count,
                csvPath = csvPath,
                jsonlPath = jsonlPath,
            };

            Debug.Log(
                $"{Marker}_START total={manifest.entries.Count} basis={manifest.basisCommit} corpus={manifest.corpusCommit} "
                + $"device={SystemInfo.deviceModel} output={outputDirectory}"
            );

            BasisProfile1SandboxDecoder decoder = null;
            var moduleStopwatch = Stopwatch.StartNew();
            bool decoderOk = BasisProfile1SandboxResources.TryCreateDecoder(
                new BasisProfile1SandboxLimits(MaximumLinearMemoryBytes, FuelLimit, FixtureTimeout),
                out decoder,
                out string decoderError
            );
            moduleStopwatch.Stop();
            summary.moduleInitializationMilliseconds = moduleStopwatch.Elapsed.TotalMilliseconds;
            if (!decoderOk)
            {
                Debug.LogError($"{Marker}_FAILED decoder={decoderError}");
                yield break;
            }

            using (decoder)
            using (var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false)))
            using (var jsonl = new StreamWriter(jsonlPath, false, new UTF8Encoding(false)))
            {
                csv.WriteLine(
                    "fixture,source_kind,mode,original_payload_bytes,payload_bytes,preparation_kind,preparation_error,load_error,"
                    + "stage_a_ms,stage_a_status,stage_a_error,stage_b_ms,stage_b_status,stage_b_diagnostic,stage_b_fuel,"
                    + "logical_header_fuel,structural_header_fuel,validation_fuel,logical_header_ms,structural_header_ms,validation_ms,"
                    + "stage_b_initial_memory,stage_b_peak_memory,stage_b_final_memory,stage_b_memory_growths,"
                    + "width,height,logical_frames,total_play_count,submitted_pixels,base_timeline_us,regular_layers,regular_layer_pixels,"
                    + "crops,crop_pixels,reference_edges,reference_pixels,saved_references,saved_reference_pixels,blends,blend_pixels,"
                    + "max_reference_chain,reference_chain_extra_pixels,preview_pixels,decode_work_candidate,decode_ms,decode_status,"
                    + "decode_fuel,decode_initial_memory,decode_peak_memory,decode_final_memory,decode_memory_growths,decoded_frames,decode_checksum"
                );
                csv.Flush();

                for (int index = 0; index < manifest.entries.Count; index++)
                {
                    BasisProfile1AndroidBenchmarkManifestEntry entry = manifest.entries[index];
                    var row = new ResultRow
                    {
                        fixture = entry.fixture,
                        sourceKind = entry.sourceKind,
                        mode = entry.mode,
                        originalPayloadBytes = entry.originalPayloadBytes,
                        preparationKind = entry.preparationKind,
                        preparationError = entry.preparationError,
                    };

                    if (!string.IsNullOrEmpty(entry.preparationError) || string.IsNullOrEmpty(entry.payloadPath))
                    {
                        summary.preparationFailures++;
                    }
                    else
                    {
                        byte[] payload = null;
                        string loadError = null;
                        yield return LoadBytes(
                            CombineStreamingUri(entry.payloadPath),
                            bytes => payload = bytes,
                            error => loadError = error
                        );
                        row.loadError = loadError;
                        if (payload == null)
                        {
                            summary.loadFailures++;
                        }
                        else
                        {
                            row.payloadBytes = payload.Length;
                            ExecutePayload(decoder, payload, row, summary);
                        }
                    }

                    string json = JsonUtility.ToJson(row, false);
                    jsonl.WriteLine(json);
                    jsonl.Flush();
                    csv.WriteLine(ToCsv(row));
                    csv.Flush();

                    Debug.Log(
                        $"{Marker}_PROGRESS {index + 1}/{manifest.entries.Count} fixture={SanitizeLog(entry.fixture)} "
                        + $"stageA={row.stageAStatus ?? "n/a"} stageB={row.stageBStatus ?? "n/a"} "
                        + $"decode={row.decodeStatus ?? "n/a"} fuel={row.stageBFuelConsumed} work={row.decodeWorkCandidate}"
                    );
                    yield return null;
                }
            }

            summary.completedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            File.WriteAllText(summaryPath, JsonUtility.ToJson(summary, true), new UTF8Encoding(false));
            Debug.Log(
                $"{Marker}_COMPLETE total={summary.totalEntries} stageBSuccess={summary.stageBSuccesses} "
                + $"decodeSuccess={summary.decodeSuccesses} prepFail={summary.preparationFailures} loadFail={summary.loadFailures} "
                + $"csv={csvPath} jsonl={jsonlPath} summary={summaryPath}"
            );
            Debug.Log($"{Marker}_ADB_PULL adb pull \"{outputDirectory}\" ./Profile1AndroidBenchmark");
        }

        private static void ExecutePayload(
            BasisProfile1SandboxDecoder decoder,
            byte[] payload,
            ResultRow row,
            Summary summary)
        {
            var stopwatch = Stopwatch.StartNew();
            bool stageAOk;
            BasisProfile1RejectionCategory stageARejection;
            string stageAError;
            using (var native = new NativeArray<byte>(payload, Allocator.Temp))
            {
                stageAOk = BasisJpegXlProfile1.TryValidateStageA(
                    native,
                    payload.Length,
                    BasisJpegXlProfile1.ProfileVersion,
                    out _,
                    out stageARejection,
                    out stageAError
                );
            }
            stopwatch.Stop();
            row.stageAMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            row.stageAStatus = stageAOk ? "Success" : stageARejection.ToString();
            row.stageAError = stageAError;
            if (!stageAOk)
            {
                summary.stageAFailures++;
                return;
            }

            stopwatch.Restart();
            BasisProfile1SandboxPreflight preflight = decoder.PreflightDetailed(
                payload,
                out BasisProfile1SandboxPreflightMetrics stageBMetrics
            );
            stopwatch.Stop();
            row.stageBMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            row.stageBStatus = preflight.Status.ToString();
            row.stageBDiagnosticReason = preflight.DiagnosticReason.ToString();
            row.stageBLogicalHeaderFuelConsumed = stageBMetrics.LogicalHeaderFuelConsumed;
            row.stageBStructuralHeaderFuelConsumed = stageBMetrics.StructuralHeaderFuelConsumed;
            row.stageBValidationFuelConsumed = stageBMetrics.ValidationFuelConsumed;
            row.stageBFuelConsumed = checked(stageBMetrics.HeaderFuelConsumed + stageBMetrics.ValidationFuelConsumed);
            row.stageBLogicalHeaderMilliseconds = stageBMetrics.LogicalHeaderMilliseconds;
            row.stageBStructuralHeaderMilliseconds = stageBMetrics.StructuralHeaderMilliseconds;
            row.stageBValidationMilliseconds = stageBMetrics.ValidationMilliseconds;
            row.stageBInitialMemoryBytes = stageBMetrics.Execution.InitialMemoryBytes;
            row.stageBPeakMemoryBytes = stageBMetrics.Execution.PeakMemoryBytes;
            row.stageBFinalMemoryBytes = stageBMetrics.Execution.FinalMemoryBytes;
            row.stageBMemoryGrowthCount = stageBMetrics.Execution.MemoryGrowthCount;
            CopyPreflight(row, preflight);

            if (preflight.Status != BasisProfile1SandboxStatus.Success)
            {
                summary.stageBFailures++;
                return;
            }
            summary.stageBSuccesses++;

            int consumedFrames = 0;
            ulong checksum = 1469598103934665603UL;
            stopwatch.Restart();
            BasisProfile1SandboxStatus decodeStatus = decoder.DecodeFramesDetailed(
                payload,
                preflight,
                (frameIndex, rgba, duration) =>
                {
                    consumedFrames++;
                    unchecked
                    {
                        checksum ^= (ulong)frameIndex;
                        checksum *= 1099511628211UL;
                        checksum ^= duration;
                        checksum *= 1099511628211UL;
                        if (rgba.Length != 0)
                        {
                            checksum ^= rgba[0];
                            checksum *= 1099511628211UL;
                            checksum ^= rgba[rgba.Length - 1];
                            checksum *= 1099511628211UL;
                        }
                    }
                    return true;
                },
                out ulong decodeFuelConsumed,
                out _,
                out BasisProfile1SandboxExecutionMetrics decodeMetrics
            );
            stopwatch.Stop();
            row.decodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            row.decodeStatus = decodeStatus.ToString();
            row.decodeFuelConsumed = decodeFuelConsumed;
            row.decodeInitialMemoryBytes = decodeMetrics.InitialMemoryBytes;
            row.decodePeakMemoryBytes = decodeMetrics.PeakMemoryBytes;
            row.decodeFinalMemoryBytes = decodeMetrics.FinalMemoryBytes;
            row.decodeMemoryGrowthCount = decodeMetrics.MemoryGrowthCount;
            row.decodedFrames = consumedFrames;
            row.decodeChecksum = checksum.ToString("x16", CultureInfo.InvariantCulture);
            if (decodeStatus == BasisProfile1SandboxStatus.Success)
                summary.decodeSuccesses++;
            else
                summary.decodeFailures++;
        }

        private static void CopyPreflight(ResultRow row, BasisProfile1SandboxPreflight preflight)
        {
            row.width = preflight.Width;
            row.height = preflight.Height;
            row.logicalFrames = preflight.LogicalFrameCount;
            row.totalPlayCount = preflight.TotalPlayCount;
            row.submittedCanvasPixels = preflight.SubmittedCanvasPixels;
            row.baseTimelineMicroseconds = preflight.BaseTimelineMicroseconds;
            row.publicRegularLayerCount = preflight.PublicRegularLayerCount;
            row.publicRegularLayerPixels = preflight.PublicRegularLayerPixels;
            row.croppedLayerCount = preflight.CroppedLayerCount;
            row.croppedLayerPixels = preflight.CroppedLayerPixels;
            row.referenceReadEdges = preflight.ReferenceReadEdges;
            row.referenceReadPixels = preflight.ReferenceReadPixels;
            row.savedReferenceCount = preflight.SavedReferenceCount;
            row.savedReferencePixels = preflight.SavedReferencePixels;
            row.blendOperationCount = preflight.BlendOperationCount;
            row.blendOperationPixels = preflight.BlendOperationPixels;
            row.maximumReferenceChainDepth = preflight.MaximumReferenceChainDepth;
            row.referenceChainExtraPixels = preflight.ReferenceChainExtraPixels;
            row.previewPixels = preflight.PreviewPixels;
            row.decodeWorkCandidate = preflight.DecodeWorkCandidate;
        }

        private static IEnumerator LoadBytes(string uri, Action<byte[]> success, Action<string> failure)
        {
            using var request = UnityWebRequest.Get(uri);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                failure?.Invoke(request.error ?? request.result.ToString());
                yield break;
            }
            success?.Invoke(request.downloadHandler.data);
        }

        private static string CombineStreamingUri(string relative)
        {
            string root = Application.streamingAssetsPath.TrimEnd('/', '\\');
            string normalized = relative.Replace('\\', '/').TrimStart('/');
            return root + "/" + StreamingRoot + "/" + normalized;
        }

        private static string ToCsv(ResultRow row)
        {
            var values = new object[]
            {
                row.fixture, row.sourceKind, row.mode, row.originalPayloadBytes, row.payloadBytes,
                row.preparationKind, row.preparationError, row.loadError,
                row.stageAMilliseconds, row.stageAStatus, row.stageAError,
                row.stageBMilliseconds, row.stageBStatus, row.stageBDiagnosticReason, row.stageBFuelConsumed,
                row.stageBLogicalHeaderFuelConsumed, row.stageBStructuralHeaderFuelConsumed, row.stageBValidationFuelConsumed,
                row.stageBLogicalHeaderMilliseconds, row.stageBStructuralHeaderMilliseconds, row.stageBValidationMilliseconds,
                row.stageBInitialMemoryBytes, row.stageBPeakMemoryBytes, row.stageBFinalMemoryBytes, row.stageBMemoryGrowthCount,
                row.width, row.height, row.logicalFrames, row.totalPlayCount, row.submittedCanvasPixels, row.baseTimelineMicroseconds,
                row.publicRegularLayerCount, row.publicRegularLayerPixels, row.croppedLayerCount, row.croppedLayerPixels,
                row.referenceReadEdges, row.referenceReadPixels, row.savedReferenceCount, row.savedReferencePixels,
                row.blendOperationCount, row.blendOperationPixels, row.maximumReferenceChainDepth,
                row.referenceChainExtraPixels, row.previewPixels, row.decodeWorkCandidate,
                row.decodeMilliseconds, row.decodeStatus, row.decodeFuelConsumed,
                row.decodeInitialMemoryBytes, row.decodePeakMemoryBytes, row.decodeFinalMemoryBytes,
                row.decodeMemoryGrowthCount, row.decodedFrames, row.decodeChecksum,
            };
            var builder = new StringBuilder(1024);
            for (int i = 0; i < values.Length; i++)
            {
                if (i != 0)
                    builder.Append(',');
                builder.Append(Csv(values[i]));
            }
            return builder.ToString();
        }

        private static string Csv(object value)
        {
            if (value == null)
                return string.Empty;
            string text = value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
            if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
                return text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static string SanitizeLog(string value) =>
            string.IsNullOrEmpty(value) ? "<empty>" : value.Replace('\n', ' ').Replace('\r', ' ');
    }
#endif
}
