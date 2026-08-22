using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Basis.ImagePickup;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Basis.ImageSandbox.Editor
{
    internal sealed class BasisProfile1BenchmarkWindow : EditorWindow
    {
        private const string MenuPath = "Basis/Debug/JPEG XL Profile 1/Benchmark";
        private const int MenuPriority = 609;

        private string _fixtureDirectory = string.Empty;
        private string _outputDirectory = string.Empty;
        private int _warmupIterations = 1;
        private int _measuredIterations = 20;
        private string _concurrencySweep = "1,2,4";
        private int _maximumLinearMemoryMiB = 256;
        private string _fuelSweep = "1000000000,4000000000,16000000000";
        private float _timeoutSeconds = 30f;
        private bool _includeSubdirectories = true;
        private Vector2 _scroll;
        private Task<BenchmarkRunResult> _runTask;
        private CancellationTokenSource _cancellation;
        private bool _preparing;
        private int _preparationCompleted;
        private int _preparationTotal;
        private string _preparationCurrent = string.Empty;
        private volatile int _benchmarkCompleted;
        private volatile int _benchmarkTotal;
        private volatile string _benchmarkProgress = string.Empty;
        private string _status = "Idle";

        [MenuItem(MenuPath, false, MenuPriority)]
        private static void OpenWindow()
        {
            var window = GetWindow<BasisProfile1BenchmarkWindow>("JPEG XL Profile 1 Benchmark");
            window.minSize = new Vector2(560, 430);
            window.Show();
        }

        private void OnEnable()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (string.IsNullOrWhiteSpace(_outputDirectory))
                _outputDirectory = Path.Combine(projectRoot, "Profile1BenchmarkResults");
            EditorApplication.update += PollRun;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollRun;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("JPEG XL Profile 1 Benchmark", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Accepts ordinary .jxl files and prepares them into the canonical Profile 1 container before timing. "
                    + "Raw codestreams and standard jxlc/jxlp containers are rewrapped without decoding or re-encoding. "
                    + "Results include Stage A, Stage B, full-decode timing, structural counters, module initialization, "
                    + "working-set peak/delta, concurrency scaling, and actual Wasmtime fuel consumption. "
                    + "A configurable fuel sweep distinguishes out-of-fuel from real wall-clock timeouts. Exact WASM "
                    + "linear-memory high-water marks are still reported as unavailable.",
                MessageType.Info
            );

            using (new EditorGUI.DisabledScope(_runTask != null || _preparing))
            {
                DrawDirectoryField("Fixture directory", ref _fixtureDirectory, "Choose Profile 1 fixture directory");
                _includeSubdirectories = EditorGUILayout.Toggle("Include subdirectories", _includeSubdirectories);
                DrawDirectoryField("Output directory", ref _outputDirectory, "Choose benchmark output directory");

                EditorGUILayout.Space();
                _warmupIterations = EditorGUILayout.IntField("Warmup iterations / worker", _warmupIterations);
                _measuredIterations = EditorGUILayout.IntField("Measured iterations / worker", _measuredIterations);
                _concurrencySweep = EditorGUILayout.TextField("Concurrency sweep", _concurrencySweep);
                _maximumLinearMemoryMiB = EditorGUILayout.IntField("WASM memory limit (MiB)", _maximumLinearMemoryMiB);
                _fuelSweep = EditorGUILayout.TextField("Fuel sweep / call", _fuelSweep);
                _timeoutSeconds = EditorGUILayout.FloatField("Timeout / call (seconds)", _timeoutSeconds);

                EditorGUILayout.Space();
                if (GUILayout.Button("Run Benchmark", GUILayout.Height(32)))
                    StartBenchmark();
            }

            if ((_runTask != null || _preparing) && GUILayout.Button("Cancel", GUILayout.Height(26)))
            {
                _status = "Cancelling...";
                _cancellation?.Cancel();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUILayout.SelectableLabel(_status, EditorStyles.textArea, GUILayout.MinHeight(70));
            if (_preparing && _preparationTotal > 0)
            {
                float progress = Mathf.Clamp01((float)_preparationCompleted / _preparationTotal);
                Rect progressRect = GUILayoutUtility.GetRect(1f, 20f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(
                    progressRect,
                    progress,
                    $"{_preparationCompleted}/{_preparationTotal}  {_preparationCurrent}"
                );
            }
            else if (_runTask != null && _benchmarkTotal > 0)
            {
                float progress = Mathf.Clamp01((float)_benchmarkCompleted / _benchmarkTotal);
                Rect progressRect = GUILayoutUtility.GetRect(1f, 20f, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(
                    progressRect,
                    progress,
                    $"{_benchmarkCompleted}/{_benchmarkTotal} benchmark groups"
                );
            }
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "Use representative real and synthetic JPEG XL files. Fixture preparation is excluded from timing and the "
                    + "original format/size plus prepared Profile 1 size are recorded in CSV/JSON. Files whose codestream cannot "
                    + "be extracted are reported as preparation failures rather than benchmarked.",
                MessageType.None
            );
            EditorGUILayout.EndScrollView();
        }

        private static void DrawDirectoryField(string label, ref string value, string dialogTitle)
        {
            EditorGUILayout.BeginHorizontal();
            value = EditorGUILayout.TextField(label, value);
            if (GUILayout.Button("Browse", GUILayout.Width(80)))
            {
                string selected = EditorUtility.OpenFolderPanel(dialogTitle, value, string.Empty);
                if (!string.IsNullOrWhiteSpace(selected))
                    value = selected;
            }
            EditorGUILayout.EndHorizontal();
        }

        private async void StartBenchmark()
        {
            if (!TryValidateSettings(out int[] concurrency, out ulong[] fuelSweep, out string error))
            {
                EditorUtility.DisplayDialog("JPEG XL Profile 1 Benchmark", error, "OK");
                return;
            }

            TextAsset decoderAsset = Resources.Load<TextAsset>(BasisProfile1SandboxResources.DecoderResourcePath);
            if (decoderAsset == null || decoderAsset.bytes == null || decoderAsset.bytes.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "JPEG XL Profile 1 Benchmark",
                    "The Profile 1 decoder resource is missing. Run Basis/Debug/JPEG XL Profile 1/Build Test Decoder first.",
                    "OK"
                );
                return;
            }

            SearchOption searchOption = _includeSubdirectories
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;
            string[] jxlFixtures = Directory.GetFiles(_fixtureDirectory, "*.jxl", searchOption);
            string[] gifFixtures = Directory.GetFiles(_fixtureDirectory, "*.gif", searchOption);
            Array.Sort(jxlFixtures, StringComparer.OrdinalIgnoreCase);
            Array.Sort(gifFixtures, StringComparer.OrdinalIgnoreCase);
            if (jxlFixtures.Length == 0 && gifFixtures.Length == 0)
            {
                EditorUtility.DisplayDialog("JPEG XL Profile 1 Benchmark", "No .jxl or .gif fixtures were found.", "OK");
                return;
            }

            Directory.CreateDirectory(_outputDirectory);
            var fixturePaths = new List<string>(jxlFixtures);
            var fixtureDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixturePreparationPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixturePreparationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixtureOriginalPayloadBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in jxlFixtures)
                fixtureDisplayNames[path] = Path.GetRelativePath(_fixtureDirectory, path).Replace('\\', '/');

            if (gifFixtures.Length > 0)
            {
                _cancellation = new CancellationTokenSource();
                _preparing = true;
                _preparationCompleted = 0;
                _preparationTotal = gifFixtures.Length;
                _preparationCurrent = "Checking cache";
                _status = $"Resolving {gifFixtures.Length} GIF fixture(s) from the Profile 1 cache...";
                Repaint();
                BasisProfile1GifBenchmarkPreparation.GifPreparationResult gifResult;
                try
                {
                    gifResult = await BasisProfile1GifBenchmarkPreparation.ConvertAsync(
                        gifFixtures,
                        _outputDirectory,
                        (completed, total, fileName, phase) =>
                        {
                            _preparationCompleted = completed;
                            _preparationTotal = total;
                            _preparationCurrent = phase + " — " + fileName;
                            _status = $"GIF preparation: {completed}/{total} — {phase}: {fileName}";
                            Repaint();
                        },
                        _cancellation.Token
                    );
                }
                catch (OperationCanceledException)
                {
                    _status = "GIF fixture preparation cancelled.";
                    _preparing = false;
                    _preparationCurrent = string.Empty;
                    _cancellation.Dispose();
                    _cancellation = null;
                    Repaint();
                    return;
                }
                finally
                {
                    _preparing = false;
                    _preparationCurrent = string.Empty;
                }

                if (!gifResult.Ok)
                {
                    EditorUtility.DisplayDialog("JPEG XL Profile 1 Benchmark", gifResult.Error, "OK");
                    _status = "GIF fixture preparation failed.";
                    _cancellation.Dispose();
                    _cancellation = null;
                    Repaint();
                    return;
                }

                foreach (string gifPath in gifFixtures)
                {
                    string displayName = Path.GetRelativePath(_fixtureDirectory, gifPath).Replace('\\', '/');
                    long originalBytes = new FileInfo(gifPath).Length;
                    if (gifResult.ConvertedByOriginal.TryGetValue(gifPath, out string convertedPath))
                    {
                        fixturePaths.Add(convertedPath);
                        fixtureDisplayNames[convertedPath] = displayName;
                        fixturePreparationPrefixes[convertedPath] = "GifLosslessFullCanvas";
                        fixtureOriginalPayloadBytes[convertedPath] = originalBytes;
                    }
                    else if (gifResult.ErrorsByOriginal.TryGetValue(gifPath, out string gifError))
                    {
                        fixturePaths.Add(gifPath);
                        fixtureDisplayNames[gifPath] = displayName;
                        fixturePreparationErrors[gifPath] = gifError;
                        fixtureOriginalPayloadBytes[gifPath] = originalBytes;
                    }
                }
            }

            string[] fixtures = fixturePaths.ToArray();
            var configuration = new BenchmarkConfiguration
            {
                FixtureDirectory = Path.GetFullPath(_fixtureDirectory),
                OutputDirectory = Path.GetFullPath(_outputDirectory),
                WarmupIterations = _warmupIterations,
                MeasuredIterations = _measuredIterations,
                Concurrency = concurrency,
                MaximumLinearMemoryBytes = (long)_maximumLinearMemoryMiB * 1024L * 1024L,
                FuelSweep = fuelSweep,
                TimeoutSeconds = _timeoutSeconds,
                DecoderBytes = (byte[])decoderAsset.bytes.Clone(),
                Fixtures = fixtures,
                FixtureDisplayNames = fixtureDisplayNames,
                FixturePreparationPrefixes = fixturePreparationPrefixes,
                FixturePreparationErrors = fixturePreparationErrors,
                FixtureOriginalPayloadBytes = fixtureOriginalPayloadBytes,
                Metadata = CaptureMetadata(),
            };

            if (_cancellation == null)
                _cancellation = new CancellationTokenSource();
            _benchmarkCompleted = 0;
            _benchmarkTotal = checked(fixtures.Length * concurrency.Length * fuelSweep.Length);
            _benchmarkProgress = $"Starting benchmark: {fixtures.Length} fixtures, concurrency {string.Join(",", concurrency)}, fuel {string.Join(",", fuelSweep)}...";
            _status = _benchmarkProgress;
            _runTask = Task.Run(
                () => RunBenchmark(configuration, ReportBenchmarkProgress, _cancellation.Token),
                _cancellation.Token
            );
            Repaint();
        }

        private bool TryValidateSettings(out int[] concurrency, out ulong[] fuelSweep, out string error)
        {
            concurrency = Array.Empty<int>();
            fuelSweep = Array.Empty<ulong>();
            error = null;
            if (string.IsNullOrWhiteSpace(_fixtureDirectory) || !Directory.Exists(_fixtureDirectory))
            {
                error = "Choose an existing fixture directory.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(_outputDirectory))
            {
                error = "Choose an output directory.";
                return false;
            }
            if (_warmupIterations < 0 || _warmupIterations > 100)
            {
                error = "Warmup iterations must be between 0 and 100.";
                return false;
            }
            if (_measuredIterations < 1 || _measuredIterations > 1000)
            {
                error = "Measured iterations must be between 1 and 1000.";
                return false;
            }
            if (_maximumLinearMemoryMiB < 32 || _maximumLinearMemoryMiB > 4096)
            {
                error = "WASM memory limit must be between 32 and 4096 MiB.";
                return false;
            }
            if (_timeoutSeconds <= 0f || _timeoutSeconds > 600f)
            {
                error = "Timeout must be greater than zero and no more than 600 seconds.";
                return false;
            }

            try
            {
                concurrency = _concurrencySweep
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();
            }
            catch (Exception)
            {
                error = "Concurrency sweep must contain integers such as 1,2,4.";
                return false;
            }

            if (concurrency.Length == 0 || concurrency.Any(value => value < 1 || value > 32))
            {
                error = "Concurrency values must be between 1 and 32.";
                return false;
            }

            try
            {
                fuelSweep = _fuelSweep
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => ulong.Parse(value, CultureInfo.InvariantCulture))
                    .Distinct()
                    .OrderBy(value => value)
                    .ToArray();
            }
            catch (Exception)
            {
                error = "Fuel sweep must contain positive integers such as 1000000000,4000000000,16000000000.";
                return false;
            }

            if (fuelSweep.Length == 0 || fuelSweep.Any(value => value == 0))
            {
                error = "Fuel values must be greater than zero.";
                return false;
            }
            return true;
        }

        private BenchmarkMetadata CaptureMetadata()
        {
            return new BenchmarkMetadata
            {
                TimestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                OperatingSystem = SystemInfo.operatingSystem,
                Processor = SystemInfo.processorType,
                ProcessorCount = SystemInfo.processorCount,
                SystemMemoryMiB = SystemInfo.systemMemorySize,
                UnityVersion = Application.unityVersion,
                LibJxlVersion = BasisProfile1SandboxDecoder.LibJxlVersion,
                LibJxlCommit = BasisProfile1SandboxDecoder.LibJxlCommit,
                EmscriptenVersion = BasisProfile1SandboxDecoder.EmscriptenVersion,
                WasmtimeVersion = BasisProfile1SandboxDecoder.WasmtimeVersion,
                NativeRuntimeSourceCommit = BasisProfile1SandboxDecoder.NativeRuntimeSourceCommit,
                DecoderSha256 = ComputeSha256(Resources.Load<TextAsset>(BasisProfile1SandboxResources.DecoderResourcePath).bytes),
            };
        }

        private static BenchmarkRunResult RunBenchmark(
            BenchmarkConfiguration configuration,
            Action<int, int, string> reportProgress,
            CancellationToken cancellationToken
        )
        {
            var result = new BenchmarkRunResult
            {
                Metadata = configuration.Metadata,
                Configuration = new SerializableConfiguration(configuration),
                Fixtures = new List<FixtureBenchmarkResult>(),
            };

            int completedGroups = 0;
            int totalGroups = checked(
                configuration.Fixtures.Length
                    * configuration.FuelSweep.Length
                    * configuration.Concurrency.Length
            );
            for (int fixtureIndex = 0; fixtureIndex < configuration.Fixtures.Length; fixtureIndex++)
            {
                string fixturePath = configuration.Fixtures[fixtureIndex];
                cancellationToken.ThrowIfCancellationRequested();
                string fixtureName = GetFixtureDisplayName(configuration, fixturePath);
                reportProgress?.Invoke(
                    completedGroups,
                    totalGroups,
                    $"Fixture {fixtureIndex + 1}/{configuration.Fixtures.Length}: {fixtureName}\nPreparing canonical Profile 1 payload..."
                );
                if (configuration.FixturePreparationErrors.TryGetValue(fixturePath, out string gifPreparationError))
                {
                    long failedOriginalPayloadBytes = configuration.FixtureOriginalPayloadBytes.TryGetValue(fixturePath, out long originalBytes)
                        ? originalBytes
                        : new FileInfo(fixturePath).Length;
                    foreach (ulong fuel in configuration.FuelSweep)
                    {
                        foreach (int concurrency in configuration.Concurrency)
                        {
                            result.Fixtures.Add(CreatePreparationFailure(
                                configuration,
                                fixturePath,
                                failedOriginalPayloadBytes,
                                fuel,
                                concurrency,
                                gifPreparationError
                            ));
                            completedGroups++;
                            reportProgress?.Invoke(
                                completedGroups,
                                totalGroups,
                                $"Fixture {fixtureIndex + 1}/{configuration.Fixtures.Length}: {fixtureName}\nGIF preparation failed; recorded fuel {fuel}, concurrency {concurrency}."
                            );
                        }
                    }
                    continue;
                }

                byte[] sourcePayload = File.ReadAllBytes(fixturePath);
                if (!TryPrepareCanonicalProfile1(sourcePayload, out PreparedFixture prepared, out string preparationError))
                {
                    foreach (ulong fuel in configuration.FuelSweep)
                    {
                        foreach (int concurrency in configuration.Concurrency)
                        {
                            result.Fixtures.Add(CreatePreparationFailure(
                                configuration,
                                fixturePath,
                                sourcePayload.LongLength,
                                fuel,
                                concurrency,
                                preparationError
                            ));
                            completedGroups++;
                            reportProgress?.Invoke(
                                completedGroups,
                                totalGroups,
                                $"Fixture {fixtureIndex + 1}/{configuration.Fixtures.Length}: {fixtureName}\nPreparation failed; recorded fuel {fuel}, concurrency {concurrency}."
                            );
                        }
                    }
                    continue;
                }

                long originalPayloadBytes = prepared.OriginalPayloadBytes;
                if (configuration.FixtureOriginalPayloadBytes.TryGetValue(fixturePath, out long originalOverride))
                    originalPayloadBytes = originalOverride;
                if (configuration.FixturePreparationPrefixes.TryGetValue(fixturePath, out string prefix))
                    prepared = new PreparedFixture(prepared.Payload, originalPayloadBytes, prefix + "+" + prepared.PreparationKind);
                else if (originalPayloadBytes != prepared.OriginalPayloadBytes)
                    prepared = new PreparedFixture(prepared.Payload, originalPayloadBytes, prepared.PreparationKind);

                for (int fuelIndex = 0; fuelIndex < configuration.FuelSweep.Length; fuelIndex++)
                {
                    ulong fuel = configuration.FuelSweep[fuelIndex];
                    for (int concurrencyIndex = 0; concurrencyIndex < configuration.Concurrency.Length; concurrencyIndex++)
                    {
                        int concurrency = configuration.Concurrency[concurrencyIndex];
                        cancellationToken.ThrowIfCancellationRequested();
                        reportProgress?.Invoke(
                            completedGroups,
                            totalGroups,
                            $"Fixture {fixtureIndex + 1}/{configuration.Fixtures.Length}: {fixtureName}\n"
                                + $"Fuel {fuelIndex + 1}/{configuration.FuelSweep.Length}: {fuel:N0}  |  "
                                + $"Concurrency {concurrencyIndex + 1}/{configuration.Concurrency.Length}: {concurrency}"
                        );
                        result.Fixtures.Add(RunFixture(configuration, fixturePath, prepared, fuel, concurrency, cancellationToken));
                        completedGroups++;
                        reportProgress?.Invoke(
                            completedGroups,
                            totalGroups,
                            $"Completed {completedGroups}/{totalGroups} benchmark groups.\n"
                                + $"Last: {fixtureName} — fuel {fuel:N0}, concurrency {concurrency}"
                        );
                    }
                }
            }

            result.OutputDirectory = configuration.OutputDirectory;
            return result;
        }

        private static FixtureBenchmarkResult CreatePreparationFailure(
            BenchmarkConfiguration configuration,
            string fixturePath,
            long originalPayloadBytes,
            ulong fuel,
            int concurrency,
            string error
        )
        {
            return new FixtureBenchmarkResult
            {
                Fixture = GetFixtureDisplayName(configuration, fixturePath),
                OriginalPayloadBytes = originalPayloadBytes,
                PayloadBytes = 0,
                PreparationKind = "Failed",
                PreparationError = error,
                FuelLimit = fuel,
                Concurrency = concurrency,
                Samples = new List<BenchmarkSample>(),
                FailureCount = configuration.MeasuredIterations * concurrency,
                FuelConsumedAvailable = false,
                WasmPeakMemoryAvailable = false,
            };
        }

        private static bool TryPrepareCanonicalProfile1(
            byte[] source,
            out PreparedFixture prepared,
            out string error
        )
        {
            prepared = null;
            error = null;
            if (source == null || source.Length < 2)
            {
                error = "JPEG XL fixture is empty or truncated.";
                return false;
            }

            if (IsCanonicalProfile1Container(source))
            {
                prepared = new PreparedFixture(source, source.LongLength, "CanonicalProfile1");
                return true;
            }

            byte[] codestream;
            string kind;
            if (source[0] == 0xff && source[1] == 0x0a)
            {
                codestream = source;
                kind = "RawCodestream";
            }
            else
            {
                if (!TryExtractCodestreamFromContainer(source, out codestream, out kind, out error))
                    return false;
            }

            if (codestream.Length < 2 || codestream[0] != 0xff || codestream[1] != 0x0a)
            {
                error = "Extracted JPEG XL codestream does not start with the expected FF 0A signature.";
                return false;
            }

            long preparedLength = 32L + 12L + codestream.LongLength;
            if (preparedLength > BasisJpegXlProfile1.MaximumPayloadBytes)
            {
                error = $"Canonical Profile 1 wrapper would be {preparedLength} bytes, above the {BasisJpegXlProfile1.MaximumPayloadBytes}-byte payload limit.";
                return false;
            }
            if (preparedLength > int.MaxValue)
            {
                error = "Canonical Profile 1 wrapper is too large for the benchmark process.";
                return false;
            }

            var payload = new byte[(int)preparedLength];
            WriteCanonicalContainerPrefix(payload);
            int offset = 32;
            WriteUInt32BigEndian(payload, offset, checked((uint)(12 + codestream.Length)));
            payload[offset + 4] = (byte)'j';
            payload[offset + 5] = (byte)'x';
            payload[offset + 6] = (byte)'l';
            payload[offset + 7] = (byte)'p';
            WriteUInt32BigEndian(payload, offset + 8, 0x80000000U);
            Buffer.BlockCopy(codestream, 0, payload, offset + 12, codestream.Length);

            prepared = new PreparedFixture(payload, source.LongLength, kind + "ToCanonicalJxlp");
            return true;
        }

        private static bool IsCanonicalProfile1Container(byte[] source)
        {
            using var native = new NativeArray<byte>(source, Allocator.Persistent);
            return BasisJpegXlProfile1.TryValidateStageA(
                native,
                source.Length,
                BasisJpegXlProfile1.ProfileVersion,
                out _,
                out _,
                out _
            );
        }

        private static bool TryExtractCodestreamFromContainer(
            byte[] source,
            out byte[] codestream,
            out string kind,
            out string error
        )
        {
            codestream = null;
            kind = null;
            error = null;
            if (source.Length < 12 || !HasJxlContainerSignature(source))
            {
                error = "Fixture is neither a raw JPEG XL codestream nor a recognized JPEG XL container.";
                return false;
            }

            var fragments = new List<ArraySegment<byte>>();
            bool sawJxlc = false;
            bool sawJxlp = false;
            uint expectedSequence = 0;
            bool sawFinalJxlp = false;
            long totalCodestreamBytes = 0;
            int offset = 12;

            while (offset < source.Length)
            {
                if (source.Length - offset < 8)
                {
                    error = "JPEG XL container ends inside a box header.";
                    return false;
                }

                uint size32 = ReadUInt32BigEndian(source, offset);
                string boxType = Encoding.ASCII.GetString(source, offset + 4, 4);
                int headerBytes = 8;
                long boxBytes;
                if (size32 == 1)
                {
                    if (source.Length - offset < 16)
                    {
                        error = "JPEG XL container ends inside an extended-size box header.";
                        return false;
                    }
                    ulong size64 = ReadUInt64BigEndian(source, offset + 8);
                    if (size64 > long.MaxValue)
                    {
                        error = "JPEG XL container box size is too large.";
                        return false;
                    }
                    boxBytes = (long)size64;
                    headerBytes = 16;
                }
                else if (size32 == 0)
                {
                    boxBytes = source.Length - offset;
                }
                else
                {
                    boxBytes = size32;
                }

                if (boxBytes < headerBytes || boxBytes > source.Length - offset)
                {
                    error = $"JPEG XL container has an invalid {boxType} box size.";
                    return false;
                }

                int contentOffset = offset + headerBytes;
                int contentBytes = checked((int)(boxBytes - headerBytes));
                if (boxType == "jxlc")
                {
                    if (sawJxlc || sawJxlp)
                    {
                        error = "JPEG XL container mixes or repeats codestream box forms.";
                        return false;
                    }
                    sawJxlc = true;
                    fragments.Add(new ArraySegment<byte>(source, contentOffset, contentBytes));
                    totalCodestreamBytes = contentBytes;
                }
                else if (boxType == "jxlp")
                {
                    if (sawJxlc || sawFinalJxlp || contentBytes < 4)
                    {
                        error = "JPEG XL container has an invalid jxlp fragment sequence.";
                        return false;
                    }
                    sawJxlp = true;
                    uint counter = ReadUInt32BigEndian(source, contentOffset);
                    uint sequence = counter & 0x7fffffffU;
                    bool isFinal = (counter & 0x80000000U) != 0;
                    if (sequence != expectedSequence)
                    {
                        error = $"JPEG XL jxlp sequence expected {expectedSequence} but found {sequence}.";
                        return false;
                    }
                    expectedSequence++;
                    sawFinalJxlp = isFinal;
                    int fragmentBytes = contentBytes - 4;
                    fragments.Add(new ArraySegment<byte>(source, contentOffset + 4, fragmentBytes));
                    totalCodestreamBytes = checked(totalCodestreamBytes + fragmentBytes);
                }

                offset = checked(offset + (int)boxBytes);
            }

            if (!sawJxlc && !sawJxlp)
            {
                error = "JPEG XL container does not contain a jxlc or jxlp codestream box.";
                return false;
            }
            if (sawJxlp && !sawFinalJxlp)
            {
                error = "JPEG XL fragmented codestream is missing the final jxlp marker.";
                return false;
            }
            if (totalCodestreamBytes <= 0 || totalCodestreamBytes > BasisJpegXlProfile1.MaximumPayloadBytes)
            {
                error = "Extracted JPEG XL codestream is empty or exceeds the Profile 1 payload budget.";
                return false;
            }

            codestream = new byte[(int)totalCodestreamBytes];
            int destination = 0;
            foreach (ArraySegment<byte> fragment in fragments)
            {
                Buffer.BlockCopy(fragment.Array, fragment.Offset, codestream, destination, fragment.Count);
                destination += fragment.Count;
            }
            kind = sawJxlc ? "StandardJxlcContainer" : "FragmentedJxlpContainer";
            return true;
        }

        private static bool HasJxlContainerSignature(byte[] source)
        {
            byte[] signature = { 0x00, 0x00, 0x00, 0x0c, 0x4a, 0x58, 0x4c, 0x20, 0x0d, 0x0a, 0x87, 0x0a };
            if (source.Length < signature.Length)
                return false;
            for (int i = 0; i < signature.Length; i++)
            {
                if (source[i] != signature[i])
                    return false;
            }
            return true;
        }

        private static void WriteCanonicalContainerPrefix(byte[] destination)
        {
            byte[] prefix =
            {
                0x00, 0x00, 0x00, 0x0c, 0x4a, 0x58, 0x4c, 0x20,
                0x0d, 0x0a, 0x87, 0x0a,
                0x00, 0x00, 0x00, 0x14, 0x66, 0x74, 0x79, 0x70,
                0x6a, 0x78, 0x6c, 0x20, 0x00, 0x00, 0x00, 0x00,
                0x6a, 0x78, 0x6c, 0x20,
            };
            Buffer.BlockCopy(prefix, 0, destination, 0, prefix.Length);
        }

        private static uint ReadUInt32BigEndian(byte[] source, int offset)
        {
            return ((uint)source[offset] << 24)
                | ((uint)source[offset + 1] << 16)
                | ((uint)source[offset + 2] << 8)
                | source[offset + 3];
        }

        private static ulong ReadUInt64BigEndian(byte[] source, int offset)
        {
            return ((ulong)ReadUInt32BigEndian(source, offset) << 32)
                | ReadUInt32BigEndian(source, offset + 4);
        }

        private static void WriteUInt32BigEndian(byte[] destination, int offset, uint value)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }

        private static FixtureBenchmarkResult RunFixture(
            BenchmarkConfiguration configuration,
            string fixturePath,
            PreparedFixture prepared,
            ulong fuel,
            int concurrency,
            CancellationToken cancellationToken
        )
        {
            byte[] payload = prepared.Payload;
            var aggregate = new FixtureBenchmarkResult
            {
                Fixture = GetFixtureDisplayName(configuration, fixturePath),
                OriginalPayloadBytes = prepared.OriginalPayloadBytes,
                PayloadBytes = payload.LongLength,
                PreparationKind = prepared.PreparationKind,
                FuelLimit = fuel,
                Concurrency = concurrency,
                Samples = new List<BenchmarkSample>(),
                FuelConsumedAvailable = true,
                WasmPeakMemoryAvailable = false,
            };

            long workingSetBefore = GetCurrentWorkingSetBytes();
            using var sampler = new WorkingSetSampler();
            sampler.Start();

            Stopwatch groupStopwatch = Stopwatch.StartNew();
            var workers = new Task<WorkerResult>[concurrency];
            for (int workerIndex = 0; workerIndex < concurrency; workerIndex++)
            {
                workers[workerIndex] = Task.Run(
                    () => RunWorker(configuration, payload, fuel, cancellationToken),
                    cancellationToken
                );
            }
            Task.WaitAll(workers, cancellationToken);
            groupStopwatch.Stop();
            sampler.Stop();
            aggregate.GroupWallMilliseconds = groupStopwatch.Elapsed.TotalMilliseconds;

            foreach (Task<WorkerResult> workerTask in workers)
            {
                WorkerResult worker = workerTask.Result;
                aggregate.ModuleInitMilliseconds.Add(worker.ModuleInitMilliseconds);
                aggregate.Samples.AddRange(worker.Samples);
            }

            long workingSetAfter = GetCurrentWorkingSetBytes();
            aggregate.WorkingSetBeforeBytes = workingSetBefore;
            aggregate.WorkingSetAfterBytes = workingSetAfter;
            aggregate.WorkingSetPeakBytes = sampler.PeakBytes;
            aggregate.WorkingSetPeakDeltaBytes = Math.Max(0, sampler.PeakBytes - workingSetBefore);
            aggregate.FinalizeSummary();
            if (aggregate.GroupWallMilliseconds > 0)
            {
                long decodedFrames = aggregate.Samples
                    .Where(sample => sample.DecodeStatus == BasisProfile1SandboxStatus.Success.ToString())
                    .Sum(sample => (long)sample.DecodedFrames);
                aggregate.AggregateDecodedFramesPerSecond = decodedFrames * 1000.0 / aggregate.GroupWallMilliseconds;
            }
            return aggregate;
        }

        private static WorkerResult RunWorker(
            BenchmarkConfiguration configuration,
            byte[] payload,
            ulong fuel,
            CancellationToken cancellationToken
        )
        {
            var limits = new BasisProfile1SandboxLimits(
                configuration.MaximumLinearMemoryBytes,
                fuel,
                TimeSpan.FromSeconds(configuration.TimeoutSeconds)
            );
            var worker = new WorkerResult();
            var stopwatch = Stopwatch.StartNew();
            BasisProfile1SandboxDecoder decoder;
            try
            {
                decoder = new BasisProfile1SandboxDecoder(configuration.DecoderBytes, limits);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Could not create Profile 1 decoder.", exception);
            }
            stopwatch.Stop();
            worker.ModuleInitMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            using (decoder)
            {
                for (int i = 0; i < configuration.WarmupIterations; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RunOne(payload, decoder, cancellationToken, false);
                }
                for (int i = 0; i < configuration.MeasuredIterations; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    worker.Samples.Add(RunOne(payload, decoder, cancellationToken, true));
                }
            }
            return worker;
        }

        private static BenchmarkSample RunOne(
            byte[] payload,
            BasisProfile1SandboxDecoder decoder,
            CancellationToken cancellationToken,
            bool measured
        )
        {
            var sample = new BenchmarkSample();
            Stopwatch stopwatch = Stopwatch.StartNew();
            using (var nativePayload = new NativeArray<byte>(payload, Allocator.Persistent))
            {
                bool stageAOk = BasisJpegXlProfile1.TryValidateStageA(
                    nativePayload,
                    payload.Length,
                    BasisJpegXlProfile1.ProfileVersion,
                    out BasisProfile1StageAResult stageA,
                    out BasisProfile1RejectionCategory stageARejection,
                    out string stageAError
                );
                stopwatch.Stop();
                sample.StageAMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                sample.StageAStatus = stageAOk ? "Success" : stageARejection.ToString();
                sample.StageAError = stageAError;
                sample.JxlpBoxCount = stageA.JxlpBoxCount;
                sample.ConcatenatedCodestreamBytes = stageA.ConcatenatedCodestreamBytes;
                if (!stageAOk)
                    return sample;
            }

            stopwatch.Restart();
            BasisProfile1SandboxPreflight preflight = decoder.Preflight(
                payload,
                out ulong stageBFuelConsumed,
                out bool stageBFuelConsumedAvailable,
                cancellationToken
            );
            stopwatch.Stop();
            sample.StageBMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            sample.StageBStatus = preflight.Status.ToString();
            sample.StageBFuelConsumed = stageBFuelConsumed;
            sample.StageBFuelConsumedAvailable = stageBFuelConsumedAvailable;
            CopyPreflight(sample, preflight);
            if (preflight.Status != BasisProfile1SandboxStatus.Success)
                return sample;

            int consumedFrames = 0;
            ulong checksum = 0;
            stopwatch.Restart();
            BasisProfile1SandboxStatus decodeStatus = decoder.DecodeFrames(
                payload,
                preflight,
                (frameIndex, rgba, duration) =>
                {
                    consumedFrames++;
                    if (rgba.Length > 0)
                    {
                        checksum = (checksum * 16777619UL) ^ rgba[0];
                        checksum = (checksum * 16777619UL) ^ rgba[rgba.Length - 1];
                    }
                    checksum ^= duration + (ulong)frameIndex;
                    return !cancellationToken.IsCancellationRequested;
                },
                out ulong decodeFuelConsumed,
                out bool decodeFuelConsumedAvailable,
                cancellationToken
            );
            stopwatch.Stop();
            sample.DecodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            sample.DecodeStatus = decodeStatus.ToString();
            sample.DecodeFuelConsumed = decodeFuelConsumed;
            sample.DecodeFuelConsumedAvailable = decodeFuelConsumedAvailable;
            sample.DecodedFrames = consumedFrames;
            sample.DecodeChecksum = checksum.ToString("x16", CultureInfo.InvariantCulture);
            if (preflight.SubmittedCanvasPixels > 0)
                sample.DecodeMillisecondsPerSubmittedMegapixel = sample.DecodeMilliseconds / (preflight.SubmittedCanvasPixels / 1_000_000.0);
            if (preflight.LogicalFrameCount > 0)
                sample.DecodeMillisecondsPerFrame = sample.DecodeMilliseconds / preflight.LogicalFrameCount;
            return sample;
        }

        private static void CopyPreflight(BenchmarkSample sample, BasisProfile1SandboxPreflight preflight)
        {
            sample.Width = preflight.Width;
            sample.Height = preflight.Height;
            sample.LogicalFrames = preflight.LogicalFrameCount;
            sample.TotalPlayCount = preflight.TotalPlayCount;
            sample.SubmittedCanvasPixels = preflight.SubmittedCanvasPixels;
            sample.BaseTimelineMicroseconds = preflight.BaseTimelineMicroseconds;
            sample.PublicRegularLayerCount = preflight.PublicRegularLayerCount;
            sample.PublicRegularLayerPixels = preflight.PublicRegularLayerPixels;
            sample.CroppedLayerCount = preflight.CroppedLayerCount;
            sample.ReferenceReadEdges = preflight.ReferenceReadEdges;
            sample.SavedReferenceCount = preflight.SavedReferenceCount;
            sample.BlendOperationCount = preflight.BlendOperationCount;
            sample.MaximumReferenceChainDepth = preflight.MaximumReferenceChainDepth;
            sample.PreviewPixels = preflight.PreviewPixels;
        }

        private void ReportBenchmarkProgress(int completed, int total, string status)
        {
            _benchmarkCompleted = completed;
            _benchmarkTotal = total;
            _benchmarkProgress = status ?? string.Empty;
        }

        private void PollRun()
        {
            Task<BenchmarkRunResult> task = _runTask;
            if (task == null)
                return;
            if (!task.IsCompleted)
            {
                string progress = _benchmarkProgress;
                if (!string.IsNullOrEmpty(progress))
                    _status = progress;
                Repaint();
                return;
            }

            _runTask = null;
            _cancellation?.Dispose();
            _cancellation = null;

            if (task.IsCanceled)
            {
                _status = "Benchmark cancelled.";
            }
            else if (task.IsFaulted)
            {
                Exception exception = task.Exception?.GetBaseException();
                _status = "Benchmark failed: " + exception?.Message;
                Debug.LogException(exception ?? new Exception("Profile 1 benchmark failed."));
            }
            else
            {
                BenchmarkRunResult result = task.Result;
                try
                {
                    WriteResults(result);
                    _status = "Benchmark complete.\nCSV: " + result.CsvPath + "\nJSON: " + result.JsonPath;
                    Debug.Log(_status);
                    EditorUtility.RevealInFinder(result.CsvPath);
                }
                catch (Exception exception)
                {
                    _status = "Benchmark completed, but writing results failed: " + exception.Message;
                    Debug.LogException(exception);
                }
            }
            Repaint();
        }

        private static void WriteResults(BenchmarkRunResult result)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string stem = Path.Combine(result.OutputDirectory, $"profile1-benchmark-{timestamp}");
            result.JsonPath = stem + ".json";
            result.CsvPath = stem + ".csv";
            File.WriteAllText(result.JsonPath, JsonUtility.ToJson(result, true));
            File.WriteAllText(result.CsvPath, BuildCsv(result));
        }

        private static string BuildCsv(BenchmarkRunResult run)
        {
            var csv = new StringBuilder();
            csv.AppendLine("fixture,original_payload_bytes,prepared_payload_bytes,preparation_kind,preparation_error,fuel_limit,concurrency,sample_count,width,height,logical_frames,submitted_pixels,regular_layers,regular_layer_pixels,crops,reference_edges,saved_references,blends,max_reference_chain,preview_pixels,module_init_mean_ms,stage_a_mean_ms,stage_a_median_ms,stage_a_p95_ms,stage_b_mean_ms,stage_b_median_ms,stage_b_p95_ms,decode_mean_ms,decode_median_ms,decode_p95_ms,decode_max_ms,decode_stddev_ms,decode_mean_ms_per_submitted_mp,decode_mean_ms_per_frame,group_wall_ms,aggregate_decoded_frames_per_second,working_set_before_bytes,working_set_after_bytes,working_set_peak_bytes,working_set_peak_delta_bytes,success_count,failure_count,fuel_consumed_available,stage_b_fuel_mean,stage_b_fuel_max,decode_fuel_mean,decode_fuel_max,wasm_peak_memory_available");
            foreach (FixtureBenchmarkResult item in run.Fixtures)
            {
                csv.Append(Csv(item.Fixture)).Append(',')
                    .Append(item.OriginalPayloadBytes).Append(',')
                    .Append(item.PayloadBytes).Append(',')
                    .Append(Csv(item.PreparationKind)).Append(',')
                    .Append(Csv(item.PreparationError)).Append(',')
                    .Append(item.FuelLimit).Append(',')
                    .Append(item.Concurrency).Append(',')
                    .Append(item.SampleCount).Append(',')
                    .Append(item.Width).Append(',')
                    .Append(item.Height).Append(',')
                    .Append(item.LogicalFrames).Append(',')
                    .Append(item.SubmittedCanvasPixels).Append(',')
                    .Append(item.PublicRegularLayerCount).Append(',')
                    .Append(item.PublicRegularLayerPixels).Append(',')
                    .Append(item.CroppedLayerCount).Append(',')
                    .Append(item.ReferenceReadEdges).Append(',')
                    .Append(item.SavedReferenceCount).Append(',')
                    .Append(item.BlendOperationCount).Append(',')
                    .Append(item.MaximumReferenceChainDepth).Append(',')
                    .Append(item.PreviewPixels).Append(',')
                    .Append(F(item.ModuleInitMeanMilliseconds)).Append(',')
                    .Append(F(item.StageAMeanMilliseconds)).Append(',')
                    .Append(F(item.StageAMedianMilliseconds)).Append(',')
                    .Append(F(item.StageAP95Milliseconds)).Append(',')
                    .Append(F(item.StageBMeanMilliseconds)).Append(',')
                    .Append(F(item.StageBMedianMilliseconds)).Append(',')
                    .Append(F(item.StageBP95Milliseconds)).Append(',')
                    .Append(F(item.DecodeMeanMilliseconds)).Append(',')
                    .Append(F(item.DecodeMedianMilliseconds)).Append(',')
                    .Append(F(item.DecodeP95Milliseconds)).Append(',')
                    .Append(F(item.DecodeMaxMilliseconds)).Append(',')
                    .Append(F(item.DecodeStdDevMilliseconds)).Append(',')
                    .Append(F(item.DecodeMeanMillisecondsPerSubmittedMegapixel)).Append(',')
                    .Append(F(item.DecodeMeanMillisecondsPerFrame)).Append(',')
                    .Append(F(item.GroupWallMilliseconds)).Append(',')
                    .Append(F(item.AggregateDecodedFramesPerSecond)).Append(',')
                    .Append(item.WorkingSetBeforeBytes).Append(',')
                    .Append(item.WorkingSetAfterBytes).Append(',')
                    .Append(item.WorkingSetPeakBytes).Append(',')
                    .Append(item.WorkingSetPeakDeltaBytes).Append(',')
                    .Append(item.SuccessCount).Append(',')
                    .Append(item.FailureCount).Append(',')
                    .Append(item.FuelConsumedAvailable ? "true" : "false").Append(',')
                    .Append(F(item.StageBFuelMean)).Append(',')
                    .Append(item.StageBFuelMax).Append(',')
                    .Append(F(item.DecodeFuelMean)).Append(',')
                    .Append(item.DecodeFuelMax).Append(',')
                    .Append(item.WasmPeakMemoryAvailable ? "true" : "false")
                    .AppendLine();
            }
            return csv.ToString();
        }

        private static string GetFixtureDisplayName(BenchmarkConfiguration configuration, string fixturePath)
        {
            if (configuration.FixtureDisplayNames != null && configuration.FixtureDisplayNames.TryGetValue(fixturePath, out string displayName))
                return displayName;
            return Path.GetRelativePath(configuration.FixtureDirectory, fixturePath).Replace('\\', '/');
        }

        private static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        [Serializable]
        private sealed class BenchmarkMetadata
        {
            public string TimestampUtc;
            public string OperatingSystem;
            public string Processor;
            public int ProcessorCount;
            public int SystemMemoryMiB;
            public string UnityVersion;
            public string LibJxlVersion;
            public string LibJxlCommit;
            public string EmscriptenVersion;
            public string WasmtimeVersion;
            public string NativeRuntimeSourceCommit;
            public string DecoderSha256;
        }

        private sealed class BenchmarkConfiguration
        {
            public string FixtureDirectory;
            public string OutputDirectory;
            public int WarmupIterations;
            public int MeasuredIterations;
            public int[] Concurrency;
            public long MaximumLinearMemoryBytes;
            public ulong[] FuelSweep;
            public float TimeoutSeconds;
            public byte[] DecoderBytes;
            public string[] Fixtures;
            public Dictionary<string, string> FixtureDisplayNames;
            public Dictionary<string, string> FixturePreparationPrefixes;
            public Dictionary<string, string> FixturePreparationErrors;
            public Dictionary<string, long> FixtureOriginalPayloadBytes;
            public BenchmarkMetadata Metadata;
        }

        [Serializable]
        private sealed class SerializableConfiguration
        {
            public string FixtureDirectory;
            public int WarmupIterations;
            public int MeasuredIterations;
            public int[] Concurrency;
            public long MaximumLinearMemoryBytes;
            public ulong[] FuelSweep;
            public float TimeoutSeconds;

            public SerializableConfiguration(BenchmarkConfiguration source)
            {
                FixtureDirectory = source.FixtureDirectory;
                WarmupIterations = source.WarmupIterations;
                MeasuredIterations = source.MeasuredIterations;
                Concurrency = source.Concurrency;
                MaximumLinearMemoryBytes = source.MaximumLinearMemoryBytes;
                FuelSweep = source.FuelSweep;
                TimeoutSeconds = source.TimeoutSeconds;
            }
        }

        [Serializable]
        private sealed class BenchmarkRunResult
        {
            public BenchmarkMetadata Metadata;
            public SerializableConfiguration Configuration;
            public List<FixtureBenchmarkResult> Fixtures;
            [NonSerialized] public string OutputDirectory;
            [NonSerialized] public string CsvPath;
            [NonSerialized] public string JsonPath;
        }

        private sealed class PreparedFixture
        {
            public readonly byte[] Payload;
            public readonly long OriginalPayloadBytes;
            public readonly string PreparationKind;

            public PreparedFixture(byte[] payload, long originalPayloadBytes, string preparationKind)
            {
                Payload = payload;
                OriginalPayloadBytes = originalPayloadBytes;
                PreparationKind = preparationKind;
            }
        }

        private sealed class WorkerResult
        {
            public double ModuleInitMilliseconds;
            public readonly List<BenchmarkSample> Samples = new List<BenchmarkSample>();
        }

        [Serializable]
        private sealed class FixtureBenchmarkResult
        {
            public string Fixture;
            public long OriginalPayloadBytes;
            public long PayloadBytes;
            public string PreparationKind;
            public string PreparationError;
            public ulong FuelLimit;
            public int Concurrency;
            public int SampleCount;
            public uint Width;
            public uint Height;
            public uint LogicalFrames;
            public ulong SubmittedCanvasPixels;
            public ulong PublicRegularLayerCount;
            public ulong PublicRegularLayerPixels;
            public ulong CroppedLayerCount;
            public ulong ReferenceReadEdges;
            public ulong SavedReferenceCount;
            public ulong BlendOperationCount;
            public ulong MaximumReferenceChainDepth;
            public ulong PreviewPixels;
            public List<double> ModuleInitMilliseconds = new List<double>();
            public double ModuleInitMeanMilliseconds;
            public double StageAMeanMilliseconds;
            public double StageAMedianMilliseconds;
            public double StageAP95Milliseconds;
            public double StageBMeanMilliseconds;
            public double StageBMedianMilliseconds;
            public double StageBP95Milliseconds;
            public double DecodeMeanMilliseconds;
            public double DecodeMedianMilliseconds;
            public double DecodeP95Milliseconds;
            public double DecodeMaxMilliseconds;
            public double DecodeStdDevMilliseconds;
            public double DecodeMeanMillisecondsPerSubmittedMegapixel;
            public double DecodeMeanMillisecondsPerFrame;
            public double GroupWallMilliseconds;
            public double AggregateDecodedFramesPerSecond;
            public long WorkingSetBeforeBytes;
            public long WorkingSetAfterBytes;
            public long WorkingSetPeakBytes;
            public long WorkingSetPeakDeltaBytes;
            public int SuccessCount;
            public int FailureCount;
            public bool FuelConsumedAvailable;
            public double StageBFuelMean;
            public ulong StageBFuelMax;
            public double DecodeFuelMean;
            public ulong DecodeFuelMax;
            public bool WasmPeakMemoryAvailable;
            public List<BenchmarkSample> Samples;

            public void FinalizeSummary()
            {
                SampleCount = Samples.Count;
                BenchmarkSample firstSuccess = Samples.FirstOrDefault(sample => sample.DecodeStatus == BasisProfile1SandboxStatus.Success.ToString());
                if (firstSuccess != null)
                {
                    Width = firstSuccess.Width;
                    Height = firstSuccess.Height;
                    LogicalFrames = firstSuccess.LogicalFrames;
                    SubmittedCanvasPixels = firstSuccess.SubmittedCanvasPixels;
                    PublicRegularLayerCount = firstSuccess.PublicRegularLayerCount;
                    PublicRegularLayerPixels = firstSuccess.PublicRegularLayerPixels;
                    CroppedLayerCount = firstSuccess.CroppedLayerCount;
                    ReferenceReadEdges = firstSuccess.ReferenceReadEdges;
                    SavedReferenceCount = firstSuccess.SavedReferenceCount;
                    BlendOperationCount = firstSuccess.BlendOperationCount;
                    MaximumReferenceChainDepth = firstSuccess.MaximumReferenceChainDepth;
                    PreviewPixels = firstSuccess.PreviewPixels;
                }

                SuccessCount = Samples.Count(sample => sample.DecodeStatus == BasisProfile1SandboxStatus.Success.ToString());
                FailureCount = SampleCount - SuccessCount;
                ulong[] stageBFuel = Samples
                    .Where(sample => sample.StageBFuelConsumedAvailable)
                    .Select(sample => sample.StageBFuelConsumed)
                    .ToArray();
                ulong[] decodeFuel = Samples
                    .Where(sample => sample.DecodeFuelConsumedAvailable)
                    .Select(sample => sample.DecodeFuelConsumed)
                    .ToArray();
                FuelConsumedAvailable = stageBFuel.Length > 0 || decodeFuel.Length > 0;
                StageBFuelMean = stageBFuel.Length == 0 ? 0 : stageBFuel.Average(value => (double)value);
                StageBFuelMax = stageBFuel.Length == 0 ? 0 : stageBFuel.Max();
                DecodeFuelMean = decodeFuel.Length == 0 ? 0 : decodeFuel.Average(value => (double)value);
                DecodeFuelMax = decodeFuel.Length == 0 ? 0 : decodeFuel.Max();
                ModuleInitMeanMilliseconds = Stats.Mean(ModuleInitMilliseconds);
                StageAMeanMilliseconds = Stats.Mean(Samples.Select(sample => sample.StageAMilliseconds));
                StageAMedianMilliseconds = Stats.Percentile(Samples.Select(sample => sample.StageAMilliseconds), 0.50);
                StageAP95Milliseconds = Stats.Percentile(Samples.Select(sample => sample.StageAMilliseconds), 0.95);
                StageBMeanMilliseconds = Stats.Mean(Samples.Where(sample => sample.StageBMilliseconds > 0).Select(sample => sample.StageBMilliseconds));
                StageBMedianMilliseconds = Stats.Percentile(Samples.Where(sample => sample.StageBMilliseconds > 0).Select(sample => sample.StageBMilliseconds), 0.50);
                StageBP95Milliseconds = Stats.Percentile(Samples.Where(sample => sample.StageBMilliseconds > 0).Select(sample => sample.StageBMilliseconds), 0.95);

                double[] decode = Samples.Where(sample => sample.DecodeMilliseconds > 0).Select(sample => sample.DecodeMilliseconds).ToArray();
                DecodeMeanMilliseconds = Stats.Mean(decode);
                DecodeMedianMilliseconds = Stats.Percentile(decode, 0.50);
                DecodeP95Milliseconds = Stats.Percentile(decode, 0.95);
                DecodeMaxMilliseconds = decode.Length == 0 ? 0 : decode.Max();
                DecodeStdDevMilliseconds = Stats.StandardDeviation(decode);
                DecodeMeanMillisecondsPerSubmittedMegapixel = Stats.Mean(Samples.Where(sample => sample.DecodeMillisecondsPerSubmittedMegapixel > 0).Select(sample => sample.DecodeMillisecondsPerSubmittedMegapixel));
                DecodeMeanMillisecondsPerFrame = Stats.Mean(Samples.Where(sample => sample.DecodeMillisecondsPerFrame > 0).Select(sample => sample.DecodeMillisecondsPerFrame));
            }
        }

        [Serializable]
        private sealed class BenchmarkSample
        {
            public double StageAMilliseconds;
            public string StageAStatus;
            public string StageAError;
            public int JxlpBoxCount;
            public long ConcatenatedCodestreamBytes;
            public double StageBMilliseconds;
            public string StageBStatus;
            public ulong StageBFuelConsumed;
            public bool StageBFuelConsumedAvailable;
            public double DecodeMilliseconds;
            public string DecodeStatus;
            public ulong DecodeFuelConsumed;
            public bool DecodeFuelConsumedAvailable;
            public double DecodeMillisecondsPerSubmittedMegapixel;
            public double DecodeMillisecondsPerFrame;
            public int DecodedFrames;
            public string DecodeChecksum;
            public uint Width;
            public uint Height;
            public uint LogicalFrames;
            public uint TotalPlayCount;
            public ulong SubmittedCanvasPixels;
            public ulong BaseTimelineMicroseconds;
            public ulong PublicRegularLayerCount;
            public ulong PublicRegularLayerPixels;
            public ulong CroppedLayerCount;
            public ulong ReferenceReadEdges;
            public ulong SavedReferenceCount;
            public ulong BlendOperationCount;
            public ulong MaximumReferenceChainDepth;
            public ulong PreviewPixels;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(
            IntPtr process,
            out ProcessMemoryCounters counters,
            uint size
        );

        private static long GetCurrentWorkingSetBytes()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    using Process process = Process.GetCurrentProcess();
                    var counters = new ProcessMemoryCounters
                    {
                        cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>(),
                    };
                    if (GetProcessMemoryInfo(process.Handle, out counters, counters.cb))
                        return checked((long)counters.WorkingSetSize.ToUInt64());
                }
                catch
                {
                }
            }

            long environmentWorkingSet = Environment.WorkingSet;
            if (environmentWorkingSet > 0)
                return environmentWorkingSet;

            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                return process.WorkingSet64;
            }
            catch
            {
                return 0;
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

        private static class Stats
        {
            public static double Mean(IEnumerable<double> values)
            {
                double[] array = values?.ToArray() ?? Array.Empty<double>();
                return array.Length == 0 ? 0 : array.Average();
            }

            public static double Percentile(IEnumerable<double> values, double percentile)
            {
                double[] array = values?.OrderBy(value => value).ToArray() ?? Array.Empty<double>();
                if (array.Length == 0)
                    return 0;
                if (array.Length == 1)
                    return array[0];
                double position = (array.Length - 1) * percentile;
                int lower = (int)Math.Floor(position);
                int upper = (int)Math.Ceiling(position);
                if (lower == upper)
                    return array[lower];
                double fraction = position - lower;
                return array[lower] + ((array[upper] - array[lower]) * fraction);
            }

            public static double StandardDeviation(IEnumerable<double> values)
            {
                double[] array = values?.ToArray() ?? Array.Empty<double>();
                if (array.Length < 2)
                    return 0;
                double mean = array.Average();
                double sum = 0;
                foreach (double value in array)
                {
                    double delta = value - mean;
                    sum += delta * delta;
                }
                return Math.Sqrt(sum / (array.Length - 1));
            }
        }
    }
}
