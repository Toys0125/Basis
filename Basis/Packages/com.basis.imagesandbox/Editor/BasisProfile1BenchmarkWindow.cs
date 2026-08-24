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

        private enum BenchmarkPreset
        {
            Quick,
            Analysis,
            Limits,
        }

        private enum JxlHandling
        {
            LocalImportAndRawConformance,
            LocalImportOnly,
            RawConformanceOnly,
        }

        private BenchmarkPreset _preset = BenchmarkPreset.Analysis;
        private JxlHandling _jxlHandling = JxlHandling.LocalImportAndRawConformance;
        private bool _includeSyntheticFixtures = true;
        private string _ffmpegPath = "ffmpeg";
        private string _fixtureDirectory = string.Empty;
        private string _outputDirectory = string.Empty;
        private int _warmupIterations = 1;
        private int _measuredIterations = 20;
        private string _concurrencySweep = "1,2,4";
        private int _maximumLinearMemoryMiB = 256;
        private string _fuelSweep = "96000000000";
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

        private static readonly SyntheticFixtureDefinition[] SyntheticFixtureDefinitions =
        {
            new SyntheticFixtureDefinition(0, "struct-crop.jxl", "synthetic/struct-crop.jxl"),
            new SyntheticFixtureDefinition(1, "struct-blend-previous.jxl", "synthetic/struct-blend-previous.jxl"),
            new SyntheticFixtureDefinition(2, "struct-saved-reference.jxl", "synthetic/struct-saved-reference.jxl"),
            new SyntheticFixtureDefinition(3, "struct-reference-chain.jxl", "synthetic/struct-reference-chain.jxl"),
            new SyntheticFixtureDefinition(4, "struct-zero-duration-layers.jxl", "synthetic/struct-zero-duration-layers.jxl"),
            new SyntheticFixtureDefinition(5, "struct-crop-blend-reference.jxl", "synthetic/struct-crop-blend-reference.jxl"),
            new SyntheticFixtureDefinition(6, "struct-stress-128-layers.jxl", "synthetic/struct-stress-128-layers.jxl"),
            new SyntheticFixtureDefinition(7, "boundary-width-2047.jxl", "synthetic/boundary-width-2047.jxl"),
            new SyntheticFixtureDefinition(8, "boundary-width-2048.jxl", "synthetic/boundary-width-2048.jxl"),
            new SyntheticFixtureDefinition(9, "boundary-width-2049.jxl", "synthetic/boundary-width-2049.jxl"),
            new SyntheticFixtureDefinition(10, "boundary-frames-511.jxl", "synthetic/boundary-frames-511.jxl"),
            new SyntheticFixtureDefinition(11, "boundary-frames-512.jxl", "synthetic/boundary-frames-512.jxl"),
            new SyntheticFixtureDefinition(12, "boundary-frames-513.jxl", "synthetic/boundary-frames-513.jxl"),
            new SyntheticFixtureDefinition(13, "boundary-submitted-below.jxl", "synthetic/boundary-submitted-below.jxl"),
            new SyntheticFixtureDefinition(14, "boundary-submitted-exact.jxl", "synthetic/boundary-submitted-exact.jxl"),
            new SyntheticFixtureDefinition(15, "boundary-submitted-above.jxl", "synthetic/boundary-submitted-above.jxl"),
            new SyntheticFixtureDefinition(16, "boundary-timeline-below.jxl", "synthetic/boundary-timeline-below.jxl"),
            new SyntheticFixtureDefinition(17, "boundary-timeline-exact.jxl", "synthetic/boundary-timeline-exact.jxl"),
            new SyntheticFixtureDefinition(18, "boundary-timeline-above.jxl", "synthetic/boundary-timeline-above.jxl"),
            new SyntheticFixtureDefinition(19, "boundary-duration-below.jxl", "synthetic/boundary-duration-below.jxl"),
            new SyntheticFixtureDefinition(20, "boundary-duration-exact.jxl", "synthetic/boundary-duration-exact.jxl"),
            new SyntheticFixtureDefinition(21, "boundary-duration-above.jxl", "synthetic/boundary-duration-above.jxl"),
            new SyntheticFixtureDefinition(22, "struct-preview.jxl", "synthetic/struct-preview.jxl"),
            new SyntheticFixtureDefinition(23, "boundary-canvas-below.jxl", "synthetic/boundary-canvas-2048x2047.jxl"),
            new SyntheticFixtureDefinition(24, "boundary-canvas-exact.jxl", "synthetic/boundary-canvas-2048x2048.jxl"),
            new SyntheticFixtureDefinition(25, "worst-case-submitted-structural.jxl", "synthetic/worst-case-submitted-structural.jxl"),
        };

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
                "One benchmark covers production-like local import (GIF, APNG, animated WebP, and JPEG XL), optional raw-JXL "
                    + "conformance, generated Profile 1 structural/boundary fixtures, Stage A/B/decode timing, detailed rejection "
                    + "reasons, header-vs-validation fuel, WASM linear-memory growth, conversion cost, compression effectiveness, "
                    + "and concurrency scaling. Local non-Profile-1 sources are decoded and re-encoded before their runtime path "
                    + "is measured; raw JXL can also be kept unchanged to exercise rejection behavior.",
                MessageType.Info
            );

            using (new EditorGUI.DisabledScope(_runTask != null || _preparing))
            {
                BenchmarkPreset newPreset = (BenchmarkPreset)EditorGUILayout.EnumPopup("Preset", _preset);
                if (newPreset != _preset)
                {
                    _preset = newPreset;
                    ApplyPreset(_preset);
                }
                _jxlHandling = (JxlHandling)EditorGUILayout.EnumPopup("JPEG XL handling", _jxlHandling);
                _includeSyntheticFixtures = EditorGUILayout.Toggle("Include generated structural/limit fixtures", _includeSyntheticFixtures);
                _ffmpegPath = EditorGUILayout.TextField("FFmpeg executable", _ffmpegPath);

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
                    StartBenchmarkComprehensive();
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
                "Preparation failures are recorded per fixture and do not stop the run. APNG/WebP local-import conversion uses "
                    + "the configured trusted-local FFmpeg executable; GIF uses BasisBurstGifDecoder and JXL uses the editor-native "
                    + "libjxl codec. The Limits preset intentionally enables a fuel sweep; Quick and Analysis use a single 96B "
                    + "ceiling so valid files normally run to completion while still reporting actual fuel consumed.",
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

        private void ApplyPreset(BenchmarkPreset preset)
        {
            switch (preset)
            {
                case BenchmarkPreset.Quick:
                    _warmupIterations = 1;
                    _measuredIterations = 3;
                    _concurrencySweep = "1";
                    _fuelSweep = "96000000000";
                    break;
                case BenchmarkPreset.Analysis:
                    _warmupIterations = 1;
                    _measuredIterations = 20;
                    _concurrencySweep = "1,2,4";
                    _fuelSweep = "96000000000";
                    break;
                case BenchmarkPreset.Limits:
                    _warmupIterations = 1;
                    _measuredIterations = 3;
                    _concurrencySweep = "1";
                    _fuelSweep = "8000000000,16000000000,32000000000,40000000000,96000000000";
                    break;
            }
        }

        private async void StartBenchmarkComprehensive()
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
            string[] apngFixtures = Directory.GetFiles(_fixtureDirectory, "*.apng", searchOption);
            string[] webpFixtures = Directory.GetFiles(_fixtureDirectory, "*.webp", searchOption);
            string[] animatedPngFixtures = Directory.GetFiles(_fixtureDirectory, "*.png", searchOption)
                .Where(BasisProfile1ExternalAnimationPreparation.IsApngFile)
                .ToArray();
            apngFixtures = apngFixtures.Concat(animatedPngFixtures)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Array.Sort(jxlFixtures, StringComparer.OrdinalIgnoreCase);
            Array.Sort(gifFixtures, StringComparer.OrdinalIgnoreCase);
            Array.Sort(apngFixtures, StringComparer.OrdinalIgnoreCase);
            Array.Sort(webpFixtures, StringComparer.OrdinalIgnoreCase);

            if (jxlFixtures.Length == 0 && gifFixtures.Length == 0 && apngFixtures.Length == 0
                && webpFixtures.Length == 0 && !_includeSyntheticFixtures)
            {
                EditorUtility.DisplayDialog(
                    "JPEG XL Profile 1 Benchmark",
                    "No .jxl, .gif, APNG, or animated WebP fixtures were found and generated fixtures are disabled.",
                    "OK"
                );
                return;
            }

            Directory.CreateDirectory(_outputDirectory);
            _cancellation = new CancellationTokenSource();
            _preparing = true;
            _preparationCompleted = 0;
            int localJxlCount = _jxlHandling == JxlHandling.RawConformanceOnly ? 0 : jxlFixtures.Length;
            int syntheticCount = _includeSyntheticFixtures ? SyntheticFixtureDefinitions.Length : 0;
            _preparationTotal = gifFixtures.Length + localJxlCount + apngFixtures.Length + webpFixtures.Length + syntheticCount;
            _preparationCurrent = "Starting";

            var fixturePaths = new List<string>();
            var fixturePayloadPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixtureDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixturePreparationPrefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixturePreparationErrors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fixtureOriginalPayloadBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var fixturePreparationMeasurements = new Dictionary<string, FixturePreparationMeasurement>(StringComparer.OrdinalIgnoreCase);
            BasisProfile1SandboxDecoder preparationDecoder = null;
            if (localJxlCount > 0 || syntheticCount > 0)
            {
                var preparationLimits = new BasisProfile1SandboxLimits(
                    (long)_maximumLinearMemoryMiB * 1024L * 1024L,
                    96_000_000_000UL,
                    TimeSpan.FromSeconds(_timeoutSeconds)
                );
                preparationDecoder = new BasisProfile1SandboxDecoder((byte[])decoderAsset.bytes.Clone(), preparationLimits);
            }

            try
            {
                if (_jxlHandling != JxlHandling.LocalImportOnly)
                {
                    foreach (string path in jxlFixtures)
                    {
                        string fixtureKey = CreateFixtureKey(path, "raw-conformance");
                        fixturePaths.Add(fixtureKey);
                        fixturePayloadPaths[fixtureKey] = path;
                        fixtureDisplayNames[fixtureKey] = RelativeFixtureName(path) + " [raw-conformance]";
                        fixturePreparationPrefixes[fixtureKey] = "RawJxlConformance";
                        fixtureOriginalPayloadBytes[fixtureKey] = new FileInfo(path).Length;
                        fixturePreparationMeasurements[fixtureKey] = new FixturePreparationMeasurement
                        {
                            Backend = "Raw JXL codestream/container preservation",
                        };
                    }
                }

                if (gifFixtures.Length > 0)
                {
                    _status = $"Preparing {gifFixtures.Length} GIF local-import fixture(s)...";
                    Repaint();
                    BasisProfile1GifBenchmarkPreparation.GifPreparationResult gifResult =
                        await BasisProfile1GifBenchmarkPreparation.ConvertAsync(
                            gifFixtures,
                            _outputDirectory,
                            (completed, total, fileName, phase) =>
                            {
                                _preparationCompleted = completed;
                                _preparationCurrent = phase + " — " + fileName;
                                _status = $"GIF preparation: {completed}/{total} — {phase}: {fileName}";
                                Repaint();
                            },
                            _cancellation.Token
                        );
                    if (!gifResult.Ok)
                        throw new InvalidOperationException(gifResult.Error);

                    foreach (string gifPath in gifFixtures)
                    {
                        string displayName = RelativeFixtureName(gifPath) + " [local-import]";
                        long originalBytes = new FileInfo(gifPath).Length;
                        string fixtureKey = CreateFixtureKey(gifPath, "local-import");
                        if (gifResult.ConvertedByOriginal.TryGetValue(gifPath, out string convertedPath))
                        {
                            fixturePaths.Add(fixtureKey);
                            fixturePayloadPaths[fixtureKey] = convertedPath;
                            fixtureDisplayNames[fixtureKey] = displayName;
                            fixturePreparationPrefixes[fixtureKey] = "GifLosslessFullCanvas";
                            fixtureOriginalPayloadBytes[fixtureKey] = originalBytes;
                            if (gifResult.MetricsByOriginal.TryGetValue(
                                    gifPath,
                                    out BasisProfile1GifBenchmarkPreparation.GifPreparationMetrics gifMetrics))
                            {
                                fixturePreparationMeasurements[fixtureKey] = new FixturePreparationMeasurement
                                {
                                    Backend = gifMetrics.Backend,
                                    CacheHit = gifMetrics.CacheHit,
                                    WasReencoded = true,
                                    DecodeMilliseconds = gifMetrics.DecodeMilliseconds,
                                    EncodeMilliseconds = gifMetrics.EncodeMilliseconds,
                                    TimelineBytes = gifMetrics.TimelineBytes,
                                    TotalMilliseconds = gifMetrics.DecodeMilliseconds + gifMetrics.EncodeMilliseconds,
                                    WorkingSetBeforeBytes = gifMetrics.WorkingSetBeforeBytes,
                                    WorkingSetAfterBytes = gifMetrics.WorkingSetAfterBytes,
                                    WorkingSetPeakBytes = gifMetrics.WorkingSetPeakBytes,
                                    WorkingSetPeakDeltaBytes = gifMetrics.WorkingSetPeakDeltaBytes,
                                };
                            }
                        }
                        else if (gifResult.ErrorsByOriginal.TryGetValue(gifPath, out string gifError))
                        {
                            fixturePaths.Add(fixtureKey);
                            fixturePayloadPaths[fixtureKey] = gifPath;
                            fixtureDisplayNames[fixtureKey] = displayName;
                            fixturePreparationErrors[fixtureKey] = gifError;
                            fixtureOriginalPayloadBytes[fixtureKey] = originalBytes;
                        }
                    }
                    _preparationCompleted = gifFixtures.Length;
                }

                if (_jxlHandling != JxlHandling.RawConformanceOnly && jxlFixtures.Length > 0)
                {
                    for (int i = 0; i < jxlFixtures.Length; i++)
                    {
                        string sourcePath = jxlFixtures[i];
                        int completedBefore = gifFixtures.Length + i;
                        UpdatePreparationProgress(completedBefore, "JXL local import", sourcePath);
                        LocalPreparationResult prepared = await PrepareLocalJxlAsync(
                            sourcePath,
                            _outputDirectory,
                            preparationDecoder,
                            _cancellation.Token
                        );
                        RegisterLocalPreparation(
                            sourcePath,
                            RelativeFixtureName(sourcePath) + " [local-import]",
                            prepared,
                            fixturePaths,
                            fixturePayloadPaths,
                            fixtureDisplayNames,
                            fixturePreparationPrefixes,
                            fixturePreparationErrors,
                            fixtureOriginalPayloadBytes,
                            fixturePreparationMeasurements
                        );
                        _preparationCompleted = completedBefore + 1;
                    }
                }

                int externalBase = gifFixtures.Length + localJxlCount;
                string[] externalFixtures = apngFixtures.Concat(webpFixtures).ToArray();
                for (int i = 0; i < externalFixtures.Length; i++)
                {
                    string sourcePath = externalFixtures[i];
                    UpdatePreparationProgress(externalBase + i, "APNG/WebP local import", sourcePath);
                    LocalPreparationResult prepared = await PrepareExternalAnimationAsync(
                        sourcePath,
                        _outputDirectory,
                        _ffmpegPath,
                        _cancellation.Token
                    );
                    RegisterLocalPreparation(
                        sourcePath,
                        RelativeFixtureName(sourcePath) + " [local-import]",
                        prepared,
                        fixturePaths,
                        fixturePayloadPaths,
                        fixtureDisplayNames,
                        fixturePreparationPrefixes,
                        fixturePreparationErrors,
                        fixtureOriginalPayloadBytes,
                        fixturePreparationMeasurements
                    );
                    _preparationCompleted = externalBase + i + 1;
                }

                if (_includeSyntheticFixtures)
                {
                    int syntheticBase = externalBase + externalFixtures.Length;
                    for (int i = 0; i < SyntheticFixtureDefinitions.Length; i++)
                    {
                        SyntheticFixtureDefinition definition = SyntheticFixtureDefinitions[i];
                        UpdatePreparationProgress(
                            syntheticBase + i,
                            "Generating synthetic fixture",
                            definition.DisplayName
                        );
                        LocalPreparationResult generated = await GenerateSyntheticFixtureAsync(
                            definition,
                            _outputDirectory,
                            preparationDecoder,
                            _cancellation.Token
                        );
                        string virtualSourcePath = Path.Combine(
                            _outputDirectory,
                            "synthetic-profile1-cache-v2",
                            definition.FileName
                        );
                        RegisterLocalPreparation(
                            virtualSourcePath,
                            definition.DisplayName,
                            generated,
                            fixturePaths,
                            fixturePayloadPaths,
                            fixtureDisplayNames,
                            fixturePreparationPrefixes,
                            fixturePreparationErrors,
                            fixtureOriginalPayloadBytes,
                            fixturePreparationMeasurements
                        );
                        _preparationCompleted = syntheticBase + i + 1;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _status = "Fixture preparation cancelled.";
                _cancellation.Dispose();
                _cancellation = null;
                return;
            }
            catch (Exception exception)
            {
                _status = "Fixture preparation failed: " + exception.Message;
                Debug.LogException(exception);
                _cancellation.Dispose();
                _cancellation = null;
                return;
            }
            finally
            {
                preparationDecoder?.Dispose();
                _preparing = false;
                _preparationCurrent = string.Empty;
                Repaint();
            }

            string[] fixtures = fixturePaths.ToArray();
            var configuration = new BenchmarkConfiguration
            {
                FixtureDirectory = Path.GetFullPath(_fixtureDirectory),
                OutputDirectory = Path.GetFullPath(_outputDirectory),
                Preset = _preset.ToString(),
                JxlHandling = _jxlHandling.ToString(),
                IncludeSyntheticFixtures = _includeSyntheticFixtures,
                FfmpegPath = _ffmpegPath,
                WarmupIterations = _warmupIterations,
                MeasuredIterations = _measuredIterations,
                Concurrency = concurrency,
                MaximumLinearMemoryBytes = (long)_maximumLinearMemoryMiB * 1024L * 1024L,
                FuelSweep = fuelSweep,
                TimeoutSeconds = _timeoutSeconds,
                DecoderBytes = (byte[])decoderAsset.bytes.Clone(),
                Fixtures = fixtures,
                FixturePayloadPaths = fixturePayloadPaths,
                FixtureDisplayNames = fixtureDisplayNames,
                FixturePreparationPrefixes = fixturePreparationPrefixes,
                FixturePreparationErrors = fixturePreparationErrors,
                FixtureOriginalPayloadBytes = fixtureOriginalPayloadBytes,
                FixturePreparationMeasurements = fixturePreparationMeasurements,
                Metadata = CaptureMetadata(),
            };

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

        private string RelativeFixtureName(string path) =>
            Path.GetRelativePath(_fixtureDirectory, path).Replace('\\', '/');

        private void UpdatePreparationProgress(int completed, string phase, string pathOrName)
        {
            _preparationCompleted = completed;
            string name = File.Exists(pathOrName) ? Path.GetFileName(pathOrName) : pathOrName;
            _preparationCurrent = phase + " — " + name;
            _status = $"Fixture preparation: {completed}/{_preparationTotal} — {phase}: {name}";
            Repaint();
        }

        private static async Task<LocalPreparationResult> PrepareLocalJxlAsync(
            string sourcePath,
            string outputRoot,
            BasisProfile1SandboxDecoder localDecoder,
            CancellationToken cancellationToken)
        {
            byte[] source = await Task.Run(() => File.ReadAllBytes(sourcePath), cancellationToken);
            long workingSetBefore = GetCurrentWorkingSetBytes();
            using var sampler = new WorkingSetSampler();
            sampler.Start();
            var total = Stopwatch.StartNew();
            var measurement = new FixturePreparationMeasurement
            {
                Backend = "editor-native libjxl local JXL import",
            };
            string cacheRoot = Path.Combine(outputRoot, "local-profile1-cache", "jxl");
            Directory.CreateDirectory(cacheRoot);
            string cachePath = Path.Combine(
                cacheRoot,
                "jxl-import-v1_" + ComputeSha256(source) + ".jxl"
            );

            try
            {
                bool alreadyProfile1 = false;
                PreparedFixture direct = null;
                if (TryPrepareCanonicalProfile1(source, out direct, out _))
                {
                    BasisProfile1SandboxPreflight preflight = await Task.Run(
                        () => localDecoder.Preflight(direct.Payload, cancellationToken),
                        cancellationToken
                    );
                    alreadyProfile1 = preflight.Status == BasisProfile1SandboxStatus.Success;
                }
                measurement.SourceAlreadyProfile1 = alreadyProfile1;
                measurement.WasReencoded = !alreadyProfile1;

                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                {
                    measurement.CacheHit = true;
                    return FinishPreparationResult(
                        true,
                        cachePath,
                        null,
                        alreadyProfile1 ? "JxlAlreadyProfile1" : "JxlTranscodedToProfile1",
                        source.LongLength,
                        measurement,
                        total,
                        sampler,
                        workingSetBefore
                    );
                }

                byte[] profile1;
                if (alreadyProfile1)
                {
                    profile1 = direct.Payload;
                }
                else
                {
                    var decodeStopwatch = Stopwatch.StartNew();
                    NativeByteResult decoded = await Task.Run(() =>
                    {
                        bool ok = BasisProfile1EditorNative.TryDecodeJxlTimeline(source, out byte[] timeline, out string decodeError);
                        return new NativeByteResult(ok, timeline, decodeError);
                    }, cancellationToken);
                    decodeStopwatch.Stop();
                    measurement.DecodeMilliseconds = decodeStopwatch.Elapsed.TotalMilliseconds;
                    if (!decoded.Ok)
                    {
                        return FinishPreparationResult(
                            false,
                            null,
                            decoded.Error,
                            "JxlTranscodedToProfile1",
                            source.LongLength,
                            measurement,
                            total,
                            sampler,
                            workingSetBefore
                        );
                    }
                    measurement.TimelineBytes = decoded.Bytes.LongLength;

                    var encodeStopwatch = Stopwatch.StartNew();
                    NativeByteResult encoded = await Task.Run(() =>
                    {
                        bool ok = BasisProfile1EditorNative.TryEncodeTimeline(decoded.Bytes, out byte[] bytes, out string encodeError);
                        return new NativeByteResult(ok, bytes, encodeError);
                    }, cancellationToken);
                    encodeStopwatch.Stop();
                    measurement.EncodeMilliseconds = encodeStopwatch.Elapsed.TotalMilliseconds;
                    if (!encoded.Ok)
                    {
                        return FinishPreparationResult(
                            false,
                            null,
                            encoded.Error,
                            "JxlTranscodedToProfile1",
                            source.LongLength,
                            measurement,
                            total,
                            sampler,
                            workingSetBefore
                        );
                    }
                    profile1 = encoded.Bytes;
                }

                await WriteFileAtomicallyAsync(cachePath, profile1, cancellationToken);
                return FinishPreparationResult(
                    true,
                    cachePath,
                    null,
                    alreadyProfile1 ? "JxlAlreadyProfile1" : "JxlTranscodedToProfile1",
                    source.LongLength,
                    measurement,
                    total,
                    sampler,
                    workingSetBefore
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return FinishPreparationResult(
                    false,
                    null,
                    "JPEG XL local import failed: " + exception.Message,
                    "JxlTranscodedToProfile1",
                    source.LongLength,
                    measurement,
                    total,
                    sampler,
                    workingSetBefore
                );
            }
        }

        private static async Task<LocalPreparationResult> PrepareExternalAnimationAsync(
            string sourcePath,
            string outputRoot,
            string ffmpegPath,
            CancellationToken cancellationToken)
        {
            byte[] source = await Task.Run(() => File.ReadAllBytes(sourcePath), cancellationToken);
            long workingSetBefore = GetCurrentWorkingSetBytes();
            using var sampler = new WorkingSetSampler();
            sampler.Start();
            var total = Stopwatch.StartNew();
            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            var measurement = new FixturePreparationMeasurement
            {
                Backend = "FFmpeg local RGBA decode + editor-native libjxl",
                WasReencoded = true,
            };
            string cacheRoot = Path.Combine(outputRoot, "local-profile1-cache", extension);
            Directory.CreateDirectory(cacheRoot);
            string cachePath = Path.Combine(
                cacheRoot,
                extension + "-import-v1_" + ComputeSha256(source) + ".jxl"
            );

            try
            {
                if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                {
                    measurement.CacheHit = true;
                    return FinishPreparationResult(
                        true,
                        cachePath,
                        null,
                        extension.ToUpperInvariant() + "TranscodedToProfile1",
                        source.LongLength,
                        measurement,
                        total,
                        sampler,
                        workingSetBefore
                    );
                }

                BasisProfile1ExternalAnimationPreparation.DecodeResult decoded =
                    await BasisProfile1ExternalAnimationPreparation.DecodeAsync(
                        sourcePath,
                        ffmpegPath,
                        cancellationToken
                    );
                measurement.DecodeMilliseconds = decoded.DecodeMilliseconds;
                measurement.Backend = decoded.Backend + " + editor-native libjxl";
                if (!decoded.Ok)
                {
                    return FinishPreparationResult(
                        false,
                        null,
                        decoded.Error,
                        extension.ToUpperInvariant() + "TranscodedToProfile1",
                        source.LongLength,
                        measurement,
                        total,
                        sampler,
                        workingSetBefore
                    );
                }
                measurement.TimelineBytes = decoded.Timeline.LongLength;

                var encodeStopwatch = Stopwatch.StartNew();
                NativeByteResult encoded = await Task.Run(() =>
                {
                    bool ok = BasisProfile1EditorNative.TryEncodeTimeline(decoded.Timeline, out byte[] bytes, out string encodeError);
                    return new NativeByteResult(ok, bytes, encodeError);
                }, cancellationToken);
                encodeStopwatch.Stop();
                measurement.EncodeMilliseconds = encodeStopwatch.Elapsed.TotalMilliseconds;
                if (!encoded.Ok)
                {
                    return FinishPreparationResult(
                        false,
                        null,
                        encoded.Error,
                        extension.ToUpperInvariant() + "TranscodedToProfile1",
                        source.LongLength,
                        measurement,
                        total,
                        sampler,
                        workingSetBefore
                    );
                }

                await WriteFileAtomicallyAsync(cachePath, encoded.Bytes, cancellationToken);
                return FinishPreparationResult(
                    true,
                    cachePath,
                    null,
                    extension.ToUpperInvariant() + "TranscodedToProfile1",
                    source.LongLength,
                    measurement,
                    total,
                    sampler,
                    workingSetBefore
                );
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return FinishPreparationResult(
                    false,
                    null,
                    "Local animation import failed: " + exception.Message,
                    extension.ToUpperInvariant() + "TranscodedToProfile1",
                    source.LongLength,
                    measurement,
                    total,
                    sampler,
                    workingSetBefore
                );
            }
        }

        private static async Task<LocalPreparationResult> GenerateSyntheticFixtureAsync(
            SyntheticFixtureDefinition definition,
            string outputRoot,
            BasisProfile1SandboxDecoder decoder,
            CancellationToken cancellationToken)
        {
            string cacheRoot = Path.Combine(outputRoot, "synthetic-profile1-cache-v2");
            Directory.CreateDirectory(cacheRoot);
            string path = Path.Combine(cacheRoot, definition.FileName);
            long workingSetBefore = GetCurrentWorkingSetBytes();
            using var sampler = new WorkingSetSampler();
            sampler.Start();
            var total = Stopwatch.StartNew();
            var measurement = new FixturePreparationMeasurement
            {
                Backend = "editor-native libjxl synthetic Profile 1 generator",
                SourceAlreadyProfile1 = true,
            };

            byte[] generatedBytes = null;
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                measurement.CacheHit = true;
                generatedBytes = await Task.Run(() => File.ReadAllBytes(path), cancellationToken);
            }

            var encodeStopwatch = Stopwatch.StartNew();
            NativeByteResult generated = generatedBytes != null
                ? new NativeByteResult(true, generatedBytes, null)
                : await Task.Run(() =>
            {
                bool ok = BasisProfile1EditorNative.TryGenerateSyntheticFixture(
                    definition.Kind,
                    out byte[] bytes,
                    out string generateError
                );
                return new NativeByteResult(ok, bytes, generateError);
            }, cancellationToken);
            encodeStopwatch.Stop();
            measurement.EncodeMilliseconds = generatedBytes == null ? encodeStopwatch.Elapsed.TotalMilliseconds : 0;
            if (!generated.Ok)
            {
                return FinishPreparationResult(
                    false,
                    null,
                    generated.Error,
                    "SyntheticProfile1",
                    0,
                    measurement,
                    total,
                    sampler,
                    workingSetBefore
                );
            }

            if (!TryValidateSyntheticFixture(definition.Kind, generated.Bytes, decoder, out string validationError))
            {
                return FinishPreparationResult(
                    false,
                    null,
                    validationError,
                    "SyntheticProfile1",
                    generated.Bytes.LongLength,
                    measurement,
                    total,
                    sampler,
                    workingSetBefore
                );
            }

            if (!measurement.CacheHit)
                await WriteFileAtomicallyAsync(path, generated.Bytes, cancellationToken);
            return FinishPreparationResult(
                true,
                path,
                null,
                "SyntheticProfile1",
                generated.Bytes.LongLength,
                measurement,
                total,
                sampler,
                workingSetBefore
            );
        }

        private static bool TryValidateSyntheticFixture(
            uint kind,
            byte[] generated,
            BasisProfile1SandboxDecoder decoder,
            out string error)
        {
            error = null;
            if (decoder == null || generated == null || generated.Length == 0)
            {
                error = "Synthetic fixture validation could not start.";
                return false;
            }
            if (!TryPrepareCanonicalProfile1(generated, out PreparedFixture prepared, out string preparationError))
            {
                error = "Synthetic fixture could not be canonicalized: " + preparationError;
                return false;
            }

            BasisProfile1SandboxPreflight preflight = decoder.Preflight(prepared.Payload);
            BasisProfile1SandboxStatus expectedStatus = BasisProfile1SandboxStatus.Success;
            BasisProfile1SandboxDiagnosticReason expectedReason = BasisProfile1SandboxDiagnosticReason.None;
            switch (kind)
            {
                case 9:
                    expectedStatus = BasisProfile1SandboxStatus.SharedLimitExceeded;
                    expectedReason = BasisProfile1SandboxDiagnosticReason.Dimensions;
                    break;
                case 12:
                    expectedStatus = BasisProfile1SandboxStatus.SharedLimitExceeded;
                    expectedReason = BasisProfile1SandboxDiagnosticReason.LogicalFrames;
                    break;
                case 15:
                    expectedStatus = BasisProfile1SandboxStatus.SharedLimitExceeded;
                    expectedReason = BasisProfile1SandboxDiagnosticReason.SubmittedPixels;
                    break;
                case 18:
                    expectedStatus = BasisProfile1SandboxStatus.SharedLimitExceeded;
                    expectedReason = BasisProfile1SandboxDiagnosticReason.Timeline;
                    break;
                case 19:
                    expectedStatus = BasisProfile1SandboxStatus.SharedLimitExceeded;
                    expectedReason = BasisProfile1SandboxDiagnosticReason.FrameDuration;
                    break;
            }

            if (preflight.Status != expectedStatus ||
                (expectedReason != BasisProfile1SandboxDiagnosticReason.None && preflight.DiagnosticReason != expectedReason))
            {
                error = $"Synthetic fixture kind {kind} produced {preflight.Status}/{preflight.DiagnosticReason}, expected {expectedStatus}/{expectedReason}.";
                return false;
            }
            if (preflight.Status != BasisProfile1SandboxStatus.Success)
                return true;

            bool structureOk = kind switch
            {
                0 => preflight.CroppedLayerCount > 0,
                1 => preflight.BlendOperationCount > 0 && preflight.ReferenceReadEdges > 0,
                2 => preflight.SavedReferenceCount > 0 && preflight.ReferenceReadEdges > 0,
                3 => preflight.SavedReferenceCount >= 3 && preflight.ReferenceReadEdges >= 3 && preflight.MaximumReferenceChainDepth >= 3,
                4 => preflight.PublicRegularLayerCount >= 9 && preflight.SavedReferenceCount > 0,
                5 => preflight.CroppedLayerCount > 0 && preflight.BlendOperationCount > 0 &&
                     preflight.SavedReferenceCount > 0 && preflight.ReferenceReadEdges > 0 &&
                     preflight.MaximumReferenceChainDepth >= 2,
                6 => preflight.PublicRegularLayerCount >= 129 && preflight.BlendOperationCount > 0 &&
                     preflight.SavedReferenceCount > 0 && preflight.ReferenceReadEdges > 0,
                7 => preflight.Width == 2047 && preflight.Height == 1,
                8 => preflight.Width == 2048 && preflight.Height == 1,
                10 => preflight.LogicalFrameCount == 511,
                11 => preflight.LogicalFrameCount == 512,
                13 => preflight.SubmittedCanvasPixels == 33_553_920UL,
                14 => preflight.SubmittedCanvasPixels == 33_554_432UL,
                16 => preflight.BaseTimelineMicroseconds == 299_999_999UL,
                17 => preflight.BaseTimelineMicroseconds == 300_000_000UL,
                20 => preflight.FrameDurationsMicroseconds.Length == 1 && preflight.FrameDurationsMicroseconds[0] == 33_334UL,
                21 => preflight.FrameDurationsMicroseconds.Length == 1 && preflight.FrameDurationsMicroseconds[0] == 33_335UL,
                22 => preflight.PreviewPixels == 4,
                23 => preflight.Width == 2048 && preflight.Height == 2047,
                24 => preflight.Width == 2048 && preflight.Height == 2048 &&
                      (ulong)preflight.Width * preflight.Height == 4_194_304UL,
                25 => preflight.Width == 256 && preflight.Height == 256 &&
                      preflight.LogicalFrameCount == 512 &&
                      preflight.SubmittedCanvasPixels == 33_554_432UL &&
                      preflight.CroppedLayerCount >= 511 &&
                      preflight.ReferenceReadEdges >= 511 &&
                      preflight.SavedReferenceCount >= 340 &&
                      preflight.BlendOperationCount >= 511 &&
                      preflight.MaximumReferenceChainDepth >= 512,
                _ => true,
            };
            if (!structureOk)
            {
                error = $"Synthetic fixture kind {kind} was accepted, but libjxl did not preserve the structural features required by the benchmark. "
                    + $"frames={preflight.LogicalFrameCount}, submitted={preflight.SubmittedCanvasPixels}, layers={preflight.PublicRegularLayerCount}, "
                    + $"layerPixels={preflight.PublicRegularLayerPixels}, crops={preflight.CroppedLayerCount}, refs={preflight.ReferenceReadEdges}, "
                    + $"saved={preflight.SavedReferenceCount}, blends={preflight.BlendOperationCount}, depth={preflight.MaximumReferenceChainDepth}, "
                    + $"preview={preflight.PreviewPixels}.";
                return false;
            }
            return true;
        }

        private static LocalPreparationResult FinishPreparationResult(
            bool ok,
            string preparedPath,
            string error,
            string preparationPrefix,
            long originalBytes,
            FixturePreparationMeasurement measurement,
            Stopwatch total,
            WorkingSetSampler sampler,
            long workingSetBefore)
        {
            total.Stop();
            sampler.Stop();
            long workingSetAfter = GetCurrentWorkingSetBytes();
            measurement.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
            measurement.WorkingSetBeforeBytes = workingSetBefore;
            measurement.WorkingSetAfterBytes = workingSetAfter;
            measurement.WorkingSetPeakBytes = sampler.PeakBytes;
            measurement.WorkingSetPeakDeltaBytes = Math.Max(0, sampler.PeakBytes - workingSetBefore);
            return new LocalPreparationResult(
                ok,
                preparedPath,
                error,
                preparationPrefix,
                originalBytes,
                measurement
            );
        }

        private static async Task WriteFileAtomicallyAsync(
            string path,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            string temporaryPath = path + ".tmp";
            await Task.Run(() =>
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
                File.WriteAllBytes(temporaryPath, bytes);
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(temporaryPath, path);
            }, cancellationToken);
        }

        private static void RegisterLocalPreparation(
            string sourcePath,
            string displayName,
            LocalPreparationResult prepared,
            List<string> fixturePaths,
            Dictionary<string, string> fixturePayloadPaths,
            Dictionary<string, string> fixtureDisplayNames,
            Dictionary<string, string> fixturePreparationPrefixes,
            Dictionary<string, string> fixturePreparationErrors,
            Dictionary<string, long> fixtureOriginalPayloadBytes,
            Dictionary<string, FixturePreparationMeasurement> fixturePreparationMeasurements)
        {
            string fixtureKey = CreateFixtureKey(sourcePath, "local-import");
            string payloadPath = prepared.Ok ? prepared.PreparedPath : sourcePath;
            fixturePaths.Add(fixtureKey);
            fixturePayloadPaths[fixtureKey] = payloadPath;
            fixtureDisplayNames[fixtureKey] = displayName;
            fixturePreparationPrefixes[fixtureKey] = prepared.PreparationPrefix;
            fixtureOriginalPayloadBytes[fixtureKey] = prepared.OriginalPayloadBytes;
            fixturePreparationMeasurements[fixtureKey] = prepared.Measurement;
            if (!prepared.Ok)
                fixturePreparationErrors[fixtureKey] = prepared.Error;
        }

        private static string CreateFixtureKey(string sourcePath, string mode) => sourcePath + "\u001f" + mode;

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
                string fixtureKey = configuration.Fixtures[fixtureIndex];
                string fixturePath = GetFixturePayloadPath(configuration, fixtureKey);
                cancellationToken.ThrowIfCancellationRequested();
                string fixtureName = GetFixtureDisplayName(configuration, fixtureKey);
                reportProgress?.Invoke(
                    completedGroups,
                    totalGroups,
                    $"Fixture {fixtureIndex + 1}/{configuration.Fixtures.Length}: {fixtureName}\nPreparing canonical Profile 1 payload..."
                );
                if (configuration.FixturePreparationErrors.TryGetValue(fixtureKey, out string gifPreparationError))
                {
                    long failedOriginalPayloadBytes = configuration.FixtureOriginalPayloadBytes.TryGetValue(fixtureKey, out long originalBytes)
                        ? originalBytes
                        : new FileInfo(fixturePath).Length;
                    foreach (ulong fuel in configuration.FuelSweep)
                    {
                        foreach (int concurrency in configuration.Concurrency)
                        {
                            result.Fixtures.Add(CreatePreparationFailure(
                                configuration,
                                fixtureKey,
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
                                fixtureKey,
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
                if (configuration.FixtureOriginalPayloadBytes.TryGetValue(fixtureKey, out long originalOverride))
                    originalPayloadBytes = originalOverride;
                if (configuration.FixturePreparationPrefixes.TryGetValue(fixtureKey, out string prefix))
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
                        result.Fixtures.Add(RunFixture(configuration, fixtureKey, prepared, fuel, concurrency, cancellationToken));
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
            var failure = new FixtureBenchmarkResult
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
            ApplyPreparationMeasurement(configuration, fixturePath, failure);
            return failure;
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

        private static void ApplyPreparationMeasurement(
            BenchmarkConfiguration configuration,
            string fixturePath,
            FixtureBenchmarkResult result)
        {
            if (configuration.FixturePreparationMeasurements == null
                || !configuration.FixturePreparationMeasurements.TryGetValue(fixturePath, out FixturePreparationMeasurement measurement)
                || measurement == null)
            {
                return;
            }

            result.PreparationBackend = measurement.Backend;
            result.PreparationCacheHit = measurement.CacheHit;
            result.WasReencoded = measurement.WasReencoded;
            result.SourceAlreadyProfile1 = measurement.SourceAlreadyProfile1;
            result.SourceDecodeMilliseconds = measurement.DecodeMilliseconds;
            result.Profile1EncodeMilliseconds = measurement.EncodeMilliseconds;
            result.PreparationTotalMilliseconds = measurement.TotalMilliseconds;
            result.TimelineBytes = measurement.TimelineBytes;
            result.PreparationWorkingSetBeforeBytes = measurement.WorkingSetBeforeBytes;
            result.PreparationWorkingSetAfterBytes = measurement.WorkingSetAfterBytes;
            result.PreparationWorkingSetPeakBytes = measurement.WorkingSetPeakBytes;
            result.PreparationWorkingSetPeakDeltaBytes = measurement.WorkingSetPeakDeltaBytes;
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
            ApplyPreparationMeasurement(configuration, fixturePath, aggregate);

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
            BasisProfile1SandboxPreflight preflight = decoder.PreflightDetailed(
                payload,
                out BasisProfile1SandboxPreflightMetrics stageBMetrics,
                cancellationToken
            );
            stopwatch.Stop();
            sample.StageBMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            sample.StageBStatus = preflight.Status.ToString();
            sample.StageBDiagnosticReason = preflight.DiagnosticReason.ToString();
            sample.StageBLogicalHeaderMilliseconds = stageBMetrics.LogicalHeaderMilliseconds;
            sample.StageBStructuralHeaderMilliseconds = stageBMetrics.StructuralHeaderMilliseconds;
            sample.StageBHeaderMilliseconds = stageBMetrics.HeaderMilliseconds;
            sample.StageBValidationMilliseconds = stageBMetrics.ValidationMilliseconds;
            sample.StageBLogicalHeaderFuelConsumed = stageBMetrics.LogicalHeaderFuelConsumed;
            sample.StageBStructuralHeaderFuelConsumed = stageBMetrics.StructuralHeaderFuelConsumed;
            sample.StageBHeaderFuelConsumed = stageBMetrics.HeaderFuelConsumed;
            sample.StageBValidationFuelConsumed = stageBMetrics.ValidationFuelConsumed;
            sample.StageBFuelConsumed = checked(stageBMetrics.HeaderFuelConsumed + stageBMetrics.ValidationFuelConsumed);
            sample.StageBFuelConsumedAvailable = stageBMetrics.FuelConsumedAvailable;
            sample.StageBInitialMemoryBytes = stageBMetrics.Execution.InitialMemoryBytes;
            sample.StageBPeakMemoryBytes = stageBMetrics.Execution.PeakMemoryBytes;
            sample.StageBFinalMemoryBytes = stageBMetrics.Execution.FinalMemoryBytes;
            sample.StageBMemoryGrowthCount = stageBMetrics.Execution.MemoryGrowthCount;
            CopyPreflight(sample, preflight);
            if (preflight.Status != BasisProfile1SandboxStatus.Success)
                return sample;

            int consumedFrames = 0;
            ulong checksum = 0;
            stopwatch.Restart();
            BasisProfile1SandboxStatus decodeStatus = decoder.DecodeFramesDetailed(
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
                out BasisProfile1SandboxExecutionMetrics decodeMetrics,
                cancellationToken
            );
            stopwatch.Stop();
            sample.DecodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            sample.DecodeStatus = decodeStatus.ToString();
            sample.DecodeFuelConsumed = decodeFuelConsumed;
            sample.DecodeFuelConsumedAvailable = decodeFuelConsumedAvailable;
            sample.DecodeInitialMemoryBytes = decodeMetrics.InitialMemoryBytes;
            sample.DecodePeakMemoryBytes = decodeMetrics.PeakMemoryBytes;
            sample.DecodeFinalMemoryBytes = decodeMetrics.FinalMemoryBytes;
            sample.DecodeMemoryGrowthCount = decodeMetrics.MemoryGrowthCount;
            sample.DecodedFrames = consumedFrames;
            sample.DecodeChecksum = checksum.ToString("x16", CultureInfo.InvariantCulture);
            if (preflight.SubmittedCanvasPixels > 0)
            {
                sample.DecodeMillisecondsPerSubmittedMegapixel = sample.DecodeMilliseconds / (preflight.SubmittedCanvasPixels / 1_000_000.0);
                sample.DecodeFuelPerSubmittedPixel = decodeFuelConsumed / (double)preflight.SubmittedCanvasPixels;
            }
            if (preflight.LogicalFrameCount > 0)
            {
                sample.DecodeMillisecondsPerFrame = sample.DecodeMilliseconds / preflight.LogicalFrameCount;
                sample.DecodeFuelPerLogicalFrame = decodeFuelConsumed / (double)preflight.LogicalFrameCount;
            }
            ulong canvasPixels = (ulong)preflight.Width * preflight.Height;
            if (canvasPixels > 0)
                sample.DecodeFuelPerCanvasPixel = decodeFuelConsumed / (double)canvasPixels;
            if (preflight.PublicRegularLayerPixels > 0)
                sample.DecodeFuelPerPublicLayerPixel = decodeFuelConsumed / (double)preflight.PublicRegularLayerPixels;
            if (preflight.BlendOperationCount > 0)
                sample.DecodeFuelPerBlendOperation = decodeFuelConsumed / (double)preflight.BlendOperationCount;
            if (preflight.ReferenceReadEdges > 0)
                sample.DecodeFuelPerReferenceEdge = decodeFuelConsumed / (double)preflight.ReferenceReadEdges;
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
            csv.AppendLine("fixture,original_payload_bytes,prepared_payload_bytes,preparation_kind,preparation_error,preparation_backend,preparation_cache_hit,was_reencoded,source_already_profile1,source_decode_ms,profile1_encode_ms,preparation_total_ms,timeline_bytes,preparation_working_set_before_bytes,preparation_working_set_after_bytes,preparation_working_set_peak_bytes,preparation_working_set_peak_delta_bytes,prepared_to_original_size_ratio,prepared_bytes_per_submitted_mp,prepared_bytes_per_logical_frame,fuel_limit,concurrency,sample_count,width,height,logical_frames,submitted_pixels,regular_layers,regular_layer_pixels,crops,reference_edges,saved_references,blends,max_reference_chain,preview_pixels,module_init_mean_ms,stage_a_mean_ms,stage_a_median_ms,stage_a_p95_ms,stage_b_mean_ms,stage_b_median_ms,stage_b_p95_ms,stage_b_diagnostic_reason,stage_b_logical_header_mean_ms,stage_b_structural_header_mean_ms,stage_b_header_mean_ms,stage_b_validation_mean_ms,stage_b_logical_header_fuel_mean,stage_b_logical_header_fuel_max,stage_b_structural_header_fuel_mean,stage_b_structural_header_fuel_max,stage_b_header_fuel_mean,stage_b_header_fuel_max,stage_b_validation_fuel_mean,stage_b_validation_fuel_max,stage_b_peak_linear_memory_bytes,stage_b_memory_growth_count_max,decode_mean_ms,decode_median_ms,decode_p95_ms,decode_max_ms,decode_stddev_ms,decode_mean_ms_per_submitted_mp,decode_mean_ms_per_frame,decode_fuel_per_canvas_pixel_mean,decode_fuel_per_submitted_pixel_mean,decode_fuel_per_logical_frame_mean,decode_fuel_per_public_layer_pixel_mean,decode_fuel_per_blend_operation_mean,decode_fuel_per_reference_edge_mean,decode_peak_linear_memory_bytes,decode_memory_growth_count_max,group_wall_ms,aggregate_decoded_frames_per_second,working_set_before_bytes,working_set_after_bytes,working_set_peak_bytes,working_set_peak_delta_bytes,success_count,failure_count,fuel_consumed_available,stage_b_fuel_mean,stage_b_fuel_max,decode_fuel_mean,decode_fuel_max,wasm_peak_memory_available");
            foreach (FixtureBenchmarkResult item in run.Fixtures)
            {
                csv.Append(Csv(item.Fixture)).Append(',')
                    .Append(item.OriginalPayloadBytes).Append(',')
                    .Append(item.PayloadBytes).Append(',')
                    .Append(Csv(item.PreparationKind)).Append(',')
                    .Append(Csv(item.PreparationError)).Append(',')
                    .Append(Csv(item.PreparationBackend)).Append(',')
                    .Append(item.PreparationCacheHit ? "true" : "false").Append(',')
                    .Append(item.WasReencoded ? "true" : "false").Append(',')
                    .Append(item.SourceAlreadyProfile1 ? "true" : "false").Append(',')
                    .Append(F(item.SourceDecodeMilliseconds)).Append(',')
                    .Append(F(item.Profile1EncodeMilliseconds)).Append(',')
                    .Append(F(item.PreparationTotalMilliseconds)).Append(',')
                    .Append(item.TimelineBytes).Append(',')
                    .Append(item.PreparationWorkingSetBeforeBytes).Append(',')
                    .Append(item.PreparationWorkingSetAfterBytes).Append(',')
                    .Append(item.PreparationWorkingSetPeakBytes).Append(',')
                    .Append(item.PreparationWorkingSetPeakDeltaBytes).Append(',')
                    .Append(F(item.PreparedToOriginalSizeRatio)).Append(',')
                    .Append(F(item.PreparedBytesPerSubmittedMegapixel)).Append(',')
                    .Append(F(item.PreparedBytesPerLogicalFrame)).Append(',')
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
                    .Append(Csv(item.StageBDiagnosticReason)).Append(',')
                    .Append(F(item.StageBLogicalHeaderMeanMilliseconds)).Append(',')
                    .Append(F(item.StageBStructuralHeaderMeanMilliseconds)).Append(',')
                    .Append(F(item.StageBHeaderMeanMilliseconds)).Append(',')
                    .Append(F(item.StageBValidationMeanMilliseconds)).Append(',')
                    .Append(F(item.StageBLogicalHeaderFuelMean)).Append(',')
                    .Append(item.StageBLogicalHeaderFuelMax).Append(',')
                    .Append(F(item.StageBStructuralHeaderFuelMean)).Append(',')
                    .Append(item.StageBStructuralHeaderFuelMax).Append(',')
                    .Append(F(item.StageBHeaderFuelMean)).Append(',')
                    .Append(item.StageBHeaderFuelMax).Append(',')
                    .Append(F(item.StageBValidationFuelMean)).Append(',')
                    .Append(item.StageBValidationFuelMax).Append(',')
                    .Append(item.StageBPeakLinearMemoryBytes).Append(',')
                    .Append(item.StageBMemoryGrowthCountMax).Append(',')
                    .Append(F(item.DecodeMeanMilliseconds)).Append(',')
                    .Append(F(item.DecodeMedianMilliseconds)).Append(',')
                    .Append(F(item.DecodeP95Milliseconds)).Append(',')
                    .Append(F(item.DecodeMaxMilliseconds)).Append(',')
                    .Append(F(item.DecodeStdDevMilliseconds)).Append(',')
                    .Append(F(item.DecodeMeanMillisecondsPerSubmittedMegapixel)).Append(',')
                    .Append(F(item.DecodeMeanMillisecondsPerFrame)).Append(',')
                    .Append(F(item.DecodeFuelPerCanvasPixelMean)).Append(',')
                    .Append(F(item.DecodeFuelPerSubmittedPixelMean)).Append(',')
                    .Append(F(item.DecodeFuelPerLogicalFrameMean)).Append(',')
                    .Append(F(item.DecodeFuelPerPublicLayerPixelMean)).Append(',')
                    .Append(F(item.DecodeFuelPerBlendOperationMean)).Append(',')
                    .Append(F(item.DecodeFuelPerReferenceEdgeMean)).Append(',')
                    .Append(item.DecodePeakLinearMemoryBytes).Append(',')
                    .Append(item.DecodeMemoryGrowthCountMax).Append(',')
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

        private static string GetFixturePayloadPath(BenchmarkConfiguration configuration, string fixtureKey)
        {
            if (configuration.FixturePayloadPaths != null && configuration.FixturePayloadPaths.TryGetValue(fixtureKey, out string payloadPath))
                return payloadPath;
            return fixtureKey;
        }

        private static string GetFixtureDisplayName(BenchmarkConfiguration configuration, string fixtureKey)
        {
            if (configuration.FixtureDisplayNames != null && configuration.FixtureDisplayNames.TryGetValue(fixtureKey, out string displayName))
                return displayName;
            string payloadPath = GetFixturePayloadPath(configuration, fixtureKey);
            return Path.GetRelativePath(configuration.FixtureDirectory, payloadPath).Replace('\\', '/');
        }

        private static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        private static string ComputeSha256(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private readonly struct SyntheticFixtureDefinition
        {
            public readonly uint Kind;
            public readonly string FileName;
            public readonly string DisplayName;

            public SyntheticFixtureDefinition(uint kind, string fileName, string displayName)
            {
                Kind = kind;
                FileName = fileName;
                DisplayName = displayName;
            }
        }

        private readonly struct NativeByteResult
        {
            public readonly bool Ok;
            public readonly byte[] Bytes;
            public readonly string Error;

            public NativeByteResult(bool ok, byte[] bytes, string error)
            {
                Ok = ok;
                Bytes = bytes;
                Error = error;
            }
        }

        private sealed class LocalPreparationResult
        {
            public readonly bool Ok;
            public readonly string PreparedPath;
            public readonly string Error;
            public readonly string PreparationPrefix;
            public readonly long OriginalPayloadBytes;
            public readonly FixturePreparationMeasurement Measurement;

            public LocalPreparationResult(
                bool ok,
                string preparedPath,
                string error,
                string preparationPrefix,
                long originalPayloadBytes,
                FixturePreparationMeasurement measurement)
            {
                Ok = ok;
                PreparedPath = preparedPath;
                Error = error;
                PreparationPrefix = preparationPrefix;
                OriginalPayloadBytes = originalPayloadBytes;
                Measurement = measurement;
            }
        }

        [Serializable]
        private sealed class FixturePreparationMeasurement
        {
            public string Backend;
            public bool CacheHit;
            public bool WasReencoded;
            public bool SourceAlreadyProfile1;
            public double DecodeMilliseconds;
            public double EncodeMilliseconds;
            public double TotalMilliseconds;
            public long TimelineBytes;
            public long WorkingSetBeforeBytes;
            public long WorkingSetAfterBytes;
            public long WorkingSetPeakBytes;
            public long WorkingSetPeakDeltaBytes;
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
            public string Preset;
            public string JxlHandling;
            public bool IncludeSyntheticFixtures;
            public string FfmpegPath;
            public int WarmupIterations;
            public int MeasuredIterations;
            public int[] Concurrency;
            public long MaximumLinearMemoryBytes;
            public ulong[] FuelSweep;
            public float TimeoutSeconds;
            public byte[] DecoderBytes;
            public string[] Fixtures;
            public Dictionary<string, string> FixturePayloadPaths;
            public Dictionary<string, string> FixtureDisplayNames;
            public Dictionary<string, string> FixturePreparationPrefixes;
            public Dictionary<string, string> FixturePreparationErrors;
            public Dictionary<string, long> FixtureOriginalPayloadBytes;
            public Dictionary<string, FixturePreparationMeasurement> FixturePreparationMeasurements;
            public BenchmarkMetadata Metadata;
        }

        [Serializable]
        private sealed class SerializableConfiguration
        {
            public string FixtureDirectory;
            public string Preset;
            public string JxlHandling;
            public bool IncludeSyntheticFixtures;
            public string FfmpegPath;
            public int WarmupIterations;
            public int MeasuredIterations;
            public int[] Concurrency;
            public long MaximumLinearMemoryBytes;
            public ulong[] FuelSweep;
            public float TimeoutSeconds;

            public SerializableConfiguration(BenchmarkConfiguration source)
            {
                FixtureDirectory = source.FixtureDirectory;
                Preset = source.Preset;
                JxlHandling = source.JxlHandling;
                IncludeSyntheticFixtures = source.IncludeSyntheticFixtures;
                FfmpegPath = source.FfmpegPath;
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
            public string PreparationBackend;
            public bool PreparationCacheHit;
            public bool WasReencoded;
            public bool SourceAlreadyProfile1;
            public double SourceDecodeMilliseconds;
            public double Profile1EncodeMilliseconds;
            public double PreparationTotalMilliseconds;
            public long TimelineBytes;
            public long PreparationWorkingSetBeforeBytes;
            public long PreparationWorkingSetAfterBytes;
            public long PreparationWorkingSetPeakBytes;
            public long PreparationWorkingSetPeakDeltaBytes;
            public double PreparedToOriginalSizeRatio;
            public double PreparedBytesPerSubmittedMegapixel;
            public double PreparedBytesPerLogicalFrame;
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
            public string StageBDiagnosticReason;
            public double StageBLogicalHeaderMeanMilliseconds;
            public double StageBStructuralHeaderMeanMilliseconds;
            public double StageBHeaderMeanMilliseconds;
            public double StageBValidationMeanMilliseconds;
            public double StageBLogicalHeaderFuelMean;
            public ulong StageBLogicalHeaderFuelMax;
            public double StageBStructuralHeaderFuelMean;
            public ulong StageBStructuralHeaderFuelMax;
            public double StageBHeaderFuelMean;
            public ulong StageBHeaderFuelMax;
            public double StageBValidationFuelMean;
            public ulong StageBValidationFuelMax;
            public ulong StageBPeakLinearMemoryBytes;
            public int StageBMemoryGrowthCountMax;
            public double DecodeMeanMilliseconds;
            public double DecodeMedianMilliseconds;
            public double DecodeP95Milliseconds;
            public double DecodeMaxMilliseconds;
            public double DecodeStdDevMilliseconds;
            public double DecodeMeanMillisecondsPerSubmittedMegapixel;
            public double DecodeMeanMillisecondsPerFrame;
            public double DecodeFuelPerCanvasPixelMean;
            public double DecodeFuelPerSubmittedPixelMean;
            public double DecodeFuelPerLogicalFrameMean;
            public double DecodeFuelPerPublicLayerPixelMean;
            public double DecodeFuelPerBlendOperationMean;
            public double DecodeFuelPerReferenceEdgeMean;
            public ulong DecodePeakLinearMemoryBytes;
            public int DecodeMemoryGrowthCountMax;
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
                    if (SubmittedCanvasPixels > 0)
                        PreparedBytesPerSubmittedMegapixel = PayloadBytes / (SubmittedCanvasPixels / 1_000_000.0);
                    if (LogicalFrames > 0)
                        PreparedBytesPerLogicalFrame = PayloadBytes / (double)LogicalFrames;
                }
                PreparedToOriginalSizeRatio = OriginalPayloadBytes > 0
                    ? PayloadBytes / (double)OriginalPayloadBytes
                    : 0;
                StageBDiagnosticReason = Samples
                    .Select(sample => sample.StageBDiagnosticReason)
                    .FirstOrDefault(reason => !string.IsNullOrEmpty(reason) && reason != BasisProfile1SandboxDiagnosticReason.None.ToString())
                    ?? BasisProfile1SandboxDiagnosticReason.None.ToString();

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
                StageBLogicalHeaderMeanMilliseconds = Stats.Mean(Samples.Where(sample => sample.StageBLogicalHeaderMilliseconds > 0).Select(sample => sample.StageBLogicalHeaderMilliseconds));
                StageBStructuralHeaderMeanMilliseconds = Stats.Mean(Samples.Where(sample => sample.StageBStructuralHeaderMilliseconds > 0).Select(sample => sample.StageBStructuralHeaderMilliseconds));
                StageBHeaderMeanMilliseconds = Stats.Mean(Samples.Where(sample => sample.StageBHeaderMilliseconds > 0).Select(sample => sample.StageBHeaderMilliseconds));
                StageBValidationMeanMilliseconds = Stats.Mean(Samples.Where(sample => sample.StageBValidationMilliseconds > 0).Select(sample => sample.StageBValidationMilliseconds));
                ulong[] logicalHeaderFuel = Samples.Where(sample => sample.StageBLogicalHeaderFuelConsumed > 0).Select(sample => sample.StageBLogicalHeaderFuelConsumed).ToArray();
                ulong[] structuralHeaderFuel = Samples.Where(sample => sample.StageBStructuralHeaderFuelConsumed > 0).Select(sample => sample.StageBStructuralHeaderFuelConsumed).ToArray();
                ulong[] headerFuel = Samples.Where(sample => sample.StageBHeaderFuelConsumed > 0).Select(sample => sample.StageBHeaderFuelConsumed).ToArray();
                ulong[] validationFuel = Samples.Where(sample => sample.StageBValidationFuelConsumed > 0).Select(sample => sample.StageBValidationFuelConsumed).ToArray();
                StageBLogicalHeaderFuelMean = logicalHeaderFuel.Length == 0 ? 0 : logicalHeaderFuel.Average(value => (double)value);
                StageBLogicalHeaderFuelMax = logicalHeaderFuel.Length == 0 ? 0 : logicalHeaderFuel.Max();
                StageBStructuralHeaderFuelMean = structuralHeaderFuel.Length == 0 ? 0 : structuralHeaderFuel.Average(value => (double)value);
                StageBStructuralHeaderFuelMax = structuralHeaderFuel.Length == 0 ? 0 : structuralHeaderFuel.Max();
                StageBHeaderFuelMean = headerFuel.Length == 0 ? 0 : headerFuel.Average(value => (double)value);
                StageBHeaderFuelMax = headerFuel.Length == 0 ? 0 : headerFuel.Max();
                StageBValidationFuelMean = validationFuel.Length == 0 ? 0 : validationFuel.Average(value => (double)value);
                StageBValidationFuelMax = validationFuel.Length == 0 ? 0 : validationFuel.Max();
                StageBPeakLinearMemoryBytes = Samples.Count == 0 ? 0 : Samples.Max(sample => sample.StageBPeakMemoryBytes);
                StageBMemoryGrowthCountMax = Samples.Count == 0 ? 0 : Samples.Max(sample => sample.StageBMemoryGrowthCount);

                double[] decode = Samples.Where(sample => sample.DecodeMilliseconds > 0).Select(sample => sample.DecodeMilliseconds).ToArray();
                DecodeMeanMilliseconds = Stats.Mean(decode);
                DecodeMedianMilliseconds = Stats.Percentile(decode, 0.50);
                DecodeP95Milliseconds = Stats.Percentile(decode, 0.95);
                DecodeMaxMilliseconds = decode.Length == 0 ? 0 : decode.Max();
                DecodeStdDevMilliseconds = Stats.StandardDeviation(decode);
                DecodeMeanMillisecondsPerSubmittedMegapixel = Stats.Mean(Samples.Where(sample => sample.DecodeMillisecondsPerSubmittedMegapixel > 0).Select(sample => sample.DecodeMillisecondsPerSubmittedMegapixel));
                DecodeMeanMillisecondsPerFrame = Stats.Mean(Samples.Where(sample => sample.DecodeMillisecondsPerFrame > 0).Select(sample => sample.DecodeMillisecondsPerFrame));
                DecodeFuelPerCanvasPixelMean = Stats.Mean(Samples.Where(sample => sample.DecodeFuelPerCanvasPixel > 0).Select(sample => sample.DecodeFuelPerCanvasPixel));
                DecodeFuelPerSubmittedPixelMean = Stats.Mean(Samples.Where(sample => sample.DecodeFuelPerSubmittedPixel > 0).Select(sample => sample.DecodeFuelPerSubmittedPixel));
                DecodeFuelPerLogicalFrameMean = Stats.Mean(Samples.Where(sample => sample.DecodeFuelPerLogicalFrame > 0).Select(sample => sample.DecodeFuelPerLogicalFrame));
                DecodeFuelPerPublicLayerPixelMean = Stats.Mean(Samples.Where(sample => sample.DecodeFuelPerPublicLayerPixel > 0).Select(sample => sample.DecodeFuelPerPublicLayerPixel));
                DecodeFuelPerBlendOperationMean = Stats.Mean(Samples.Where(sample => sample.DecodeFuelPerBlendOperation > 0).Select(sample => sample.DecodeFuelPerBlendOperation));
                DecodeFuelPerReferenceEdgeMean = Stats.Mean(Samples.Where(sample => sample.DecodeFuelPerReferenceEdge > 0).Select(sample => sample.DecodeFuelPerReferenceEdge));
                DecodePeakLinearMemoryBytes = Samples.Count == 0 ? 0 : Samples.Max(sample => sample.DecodePeakMemoryBytes);
                DecodeMemoryGrowthCountMax = Samples.Count == 0 ? 0 : Samples.Max(sample => sample.DecodeMemoryGrowthCount);
                WasmPeakMemoryAvailable = StageBPeakLinearMemoryBytes > 0 || DecodePeakLinearMemoryBytes > 0;
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
            public string StageBDiagnosticReason;
            public double StageBLogicalHeaderMilliseconds;
            public double StageBStructuralHeaderMilliseconds;
            public double StageBHeaderMilliseconds;
            public double StageBValidationMilliseconds;
            public ulong StageBLogicalHeaderFuelConsumed;
            public ulong StageBStructuralHeaderFuelConsumed;
            public ulong StageBHeaderFuelConsumed;
            public ulong StageBValidationFuelConsumed;
            public ulong StageBFuelConsumed;
            public bool StageBFuelConsumedAvailable;
            public ulong StageBInitialMemoryBytes;
            public ulong StageBPeakMemoryBytes;
            public ulong StageBFinalMemoryBytes;
            public int StageBMemoryGrowthCount;
            public double DecodeMilliseconds;
            public string DecodeStatus;
            public ulong DecodeFuelConsumed;
            public bool DecodeFuelConsumedAvailable;
            public ulong DecodeInitialMemoryBytes;
            public ulong DecodePeakMemoryBytes;
            public ulong DecodeFinalMemoryBytes;
            public int DecodeMemoryGrowthCount;
            public double DecodeMillisecondsPerSubmittedMegapixel;
            public double DecodeMillisecondsPerFrame;
            public double DecodeFuelPerCanvasPixel;
            public double DecodeFuelPerSubmittedPixel;
            public double DecodeFuelPerLogicalFrame;
            public double DecodeFuelPerPublicLayerPixel;
            public double DecodeFuelPerBlendOperation;
            public double DecodeFuelPerReferenceEdge;
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
