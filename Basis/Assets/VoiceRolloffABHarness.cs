using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Device_Management;
using Basis.Scripts.Networking;
using Basis.Scripts.Networking.Receivers;
using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;

public sealed class VoiceRolloffABHarness : MonoBehaviour
{
    private const int SampleRate = 48000;
    private const float MinDistance = 0.5f;
    private const float MaxDistance = 25f;
    private const int WarmupCallbacks = 4;
    private const int MinimumCaptureSampleFrames = SampleRate / 4;
    private const float CaptureTimeoutSeconds = 5f;

    [SerializeField] private float bootTimeoutSeconds = 180f;
    [SerializeField] private float postBootSettleSeconds = 5f;

    private static readonly AnimationCurve LegacyRolloff = new AnimationCurve(
        new Keyframe(0.036f, 1f, -2.214f, -2.214f),
        new Keyframe(0.239f, 0.575f, -2.305f, -2.305f),
        new Keyframe(0.372f, 0.328f, -1.068f, -1.068f),
        new Keyframe(0.621f, 0.144f, -0.515f, -0.515f),
        new Keyframe(1f, 0f, -0.031f, -0.031f));

    private string status = "Voice rolloff A/B harness starting...";

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
    }

    private IEnumerator Start()
    {
        string resultPath = Path.Combine(Application.persistentDataPath, "VoiceRolloffABResults.txt");
        float waitStarted = Time.realtimeSinceStartup;

        status = "Waiting for Basis boot to finish...";
        Debug.Log($"[Voice Rolloff A/B] {status}");

        while (!BasisDeviceManagement.OnInitializationComplete ||
               !BasisLocalPlayer.PlayerReady ||
               !BasisNetworkManagement.IsInitialized)
        {
            if (Time.realtimeSinceStartup - waitStarted > bootTimeoutSeconds)
            {
                Fail(resultPath,
                    $"Timed out waiting for Basis boot after {bootTimeoutSeconds:F0}s. " +
                    $"Device={BasisDeviceManagement.OnInitializationComplete}, " +
                    $"Player={BasisLocalPlayer.PlayerReady}, Network={BasisNetworkManagement.IsInitialized}.");
                yield break;
            }
            yield return null;
        }

        status = $"Basis boot finished. Settling for {postBootSettleSeconds:F1}s...";
        Debug.Log($"[Voice Rolloff A/B] {status}");
        yield return new WaitForSecondsRealtime(postBootSettleSeconds);

        yield return RunCapture(resultPath, Time.realtimeSinceStartup - waitStarted);
    }

    private IEnumerator RunCapture(string resultPath, float bootWaitSeconds)
    {
        AudioListener[] existingListeners = FindObjectsByType<AudioListener>();
        AudioSource[] existingSources = FindObjectsByType<AudioSource>();
        bool[] listenerEnabled = CaptureEnabled(existingListeners);
        bool[] sourceEnabled = CaptureEnabled(existingSources);
        bool previousListenerPause = AudioListener.pause;
        float previousListenerVolume = AudioListener.volume;

        var listenerObject = new GameObject("Voice A/B Listener");
        var sourceObject = new GameObject("Voice A/B Source");
        AudioClip clip = null;

        try
        {
            status = "Preparing isolated listener capture...";
            Debug.Log($"[Voice Rolloff A/B] {status}");

            SetEnabled(existingListeners, false);
            SetEnabled(existingSources, false);
            AudioListener.pause = false;
            AudioListener.volume = 1f;

            listenerObject.transform.position = Vector3.zero;
            listenerObject.AddComponent<AudioListener>();
            VoiceRolloffABListenerTap tap = listenerObject.AddComponent<VoiceRolloffABListenerTap>();

            AudioSource source = sourceObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.volume = 1f;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.minDistance = MinDistance;
            source.maxDistance = MaxDistance;
            source.spatialize = false;
            source.bypassEffects = true;
            source.bypassListenerEffects = false;
            source.bypassReverbZones = true;

            if (!TryBuildToneClip(out clip, out string clipError))
            {
                Fail(resultPath, clipError);
                yield break;
            }
            source.clip = clip;

            status = "Recording 2D control...";
            Debug.Log($"[Voice Rolloff A/B] {status}");
            float twoDimensionalRms = 0f;
            long twoDimensionalFrames = 0;
            int listenerChannels = 0;
            string captureError = null;
            source.spatialBlend = 0f;
            yield return MeasureListenerOutput(source, tap, "2D control", 0f, null,
                (rms, frames, channels) =>
                {
                    twoDimensionalRms = rms;
                    twoDimensionalFrames = frames;
                    listenerChannels = channels;
                },
                error => captureError = error);
            if (captureError != null)
            {
                Fail(resultPath, captureError);
                yield break;
            }
            if (twoDimensionalRms <= 1e-5f)
            {
                Fail(resultPath,
                    $"AudioListener tap captured silence for the 2D control signal (RMS {twoDimensionalRms:F8}).");
                yield break;
            }
            source.spatialBlend = 1f;

            AnimationCurve naturalRolloff = BasisVoiceAcoustics.BuildRolloffCurve(MinDistance, MaxDistance);
            var report = new StringBuilder();
            report.AppendLine("Basis Voice Rolloff A/B - normal Play Mode AudioListener OnAudioFilterRead capture");
            report.AppendLine($"UTC: {DateTime.UtcNow:O}");
            report.AppendLine($"Basis boot wait: {bootWaitSeconds:F2}s");
            report.AppendLine($"Post-boot settle: {postBootSettleSeconds:F2}s");
            report.AppendLine($"Output sample rate: {AudioSettings.outputSampleRate} Hz");
            report.AppendLine($"Listener channels: {listenerChannels}");
            report.AppendLine($"2D validation frames: {twoDimensionalFrames}");
            report.AppendLine($"2D validation RMS: {twoDimensionalRms:F6}");
            report.AppendLine();
            report.AppendLine("distance | legacy RMS | natural RMS | natural/legacy dB");

            foreach (float distance in new[] { 1f, 2f, 5f, 10f })
            {
                float legacyRms = 0f;
                float naturalRms = 0f;

                status = $"Recording Legacy at {distance:F1} m...";
                Debug.Log($"[Voice Rolloff A/B] {status}");
                captureError = null;
                yield return MeasureListenerOutput(source, tap, $"Legacy {distance:F1} m", distance,
                    LegacyRolloff,
                    (rms, _, __) => legacyRms = rms,
                    error => captureError = error);
                if (captureError != null)
                {
                    Fail(resultPath, captureError);
                    yield break;
                }

                status = $"Recording Natural at {distance:F1} m...";
                Debug.Log($"[Voice Rolloff A/B] {status}");
                captureError = null;
                yield return MeasureListenerOutput(source, tap, $"Natural {distance:F1} m", distance,
                    naturalRolloff,
                    (rms, _, __) => naturalRms = rms,
                    error => captureError = error);
                if (captureError != null)
                {
                    Fail(resultPath, captureError);
                    yield break;
                }

                if (legacyRms <= 1e-7f)
                {
                    Fail(resultPath, $"Legacy output was silent at {distance:F1} m.");
                    yield break;
                }

                float differenceDb = Db(naturalRms / legacyRms);
                report.AppendLine(
                    $"{distance,8:F1} | {legacyRms,10:F6} | {naturalRms,11:F6} | {differenceDb,17:F2}");
            }

            string text = report.ToString();
            if (!TryWriteText(resultPath, text, out string writeError))
            {
                Fail(resultPath, writeError);
                yield break;
            }

            status = $"Finished. Results: {resultPath}";
            Debug.Log($"[Voice Rolloff A/B]\n{text}\nResults saved to: {resultPath}");
        }
        finally
        {
            if (clip != null) Destroy(clip);
            Destroy(sourceObject);
            Destroy(listenerObject);

            AudioListener.pause = previousListenerPause;
            AudioListener.volume = previousListenerVolume;
            RestoreEnabled(existingSources, sourceEnabled);
            RestoreEnabled(existingListeners, listenerEnabled);
        }
    }

    private static IEnumerator MeasureListenerOutput(
        AudioSource source,
        VoiceRolloffABListenerTap tap,
        string label,
        float distance,
        AnimationCurve rolloff,
        Action<float, long, int> result,
        Action<string> error)
    {
        source.Stop();
        source.transform.position = new Vector3(distance, 0f, 0f);
        source.timeSamples = 0;
        if (rolloff != null)
        {
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, rolloff);
        }

        tap.BeginCapture();
        source.Play();

        float timeoutAt = Time.realtimeSinceStartup + CaptureTimeoutSeconds;
        while (tap.CallbackCount < WarmupCallbacks)
        {
            if (Time.realtimeSinceStartup >= timeoutAt)
            {
                tap.EndCapture(out _, out _, out _);
                source.Stop();
                error($"AudioListener OnAudioFilterRead never received warmup audio for {label}.");
                yield break;
            }
            yield return null;
        }

        tap.BeginCapture();
        timeoutAt = Time.realtimeSinceStartup + CaptureTimeoutSeconds;
        while (tap.SampleFrames < MinimumCaptureSampleFrames)
        {
            if (Time.realtimeSinceStartup >= timeoutAt)
            {
                tap.EndCapture(out long timedOutFrames, out int timedOutChannels, out int callbacks);
                source.Stop();
                error(
                    $"AudioListener capture timed out for {label}: " +
                    $"frames={timedOutFrames}, channels={timedOutChannels}, callbacks={callbacks}.");
                yield break;
            }
            yield return null;
        }

        float rms = tap.EndCapture(out long capturedFrames, out int capturedChannels, out _);
        source.Stop();
        result(rms, capturedFrames, capturedChannels);
    }

    private static bool TryBuildToneClip(out AudioClip clip, out string error)
    {
        const int frames = SampleRate * 2;
        var samples = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / SampleRate;
            samples[i] = 0.2f * (float)(
                0.60 * Math.Sin(2.0 * Math.PI * 250.0 * t) +
                0.30 * Math.Sin(2.0 * Math.PI * 1000.0 * t + 0.4) +
                0.10 * Math.Sin(2.0 * Math.PI * 4000.0 * t + 1.1));
        }

        clip = AudioClip.Create("Voice A/B deterministic signal", frames, 1, SampleRate, false);
        if (!clip.SetData(samples, 0))
        {
            Destroy(clip);
            clip = null;
            error = "Failed to load the deterministic A/B signal into the AudioClip.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryWriteText(string path, string text, out string error)
    {
        try
        {
            File.WriteAllText(path, text);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not write results to {path}: {ex}";
            return false;
        }
    }

    private static bool[] CaptureEnabled<T>(T[] components) where T : Behaviour
    {
        var states = new bool[components.Length];
        for (int i = 0; i < components.Length; i++)
        {
            states[i] = components[i] != null && components[i].enabled;
        }
        return states;
    }

    private static void SetEnabled<T>(T[] components, bool enabled) where T : Behaviour
    {
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i] != null) components[i].enabled = enabled;
        }
    }

    private static void RestoreEnabled<T>(T[] components, bool[] states) where T : Behaviour
    {
        int count = Math.Min(components.Length, states.Length);
        for (int i = 0; i < count; i++)
        {
            if (components[i] != null) components[i].enabled = states[i];
        }
    }

    private void Fail(string resultPath, string message)
    {
        status = $"FAILED: {message}";
        string text = $"Basis Voice Rolloff A/B FAILED\nUTC: {DateTime.UtcNow:O}\n{message}\n";
        try
        {
            File.WriteAllText(resultPath, text);
        }
        catch (Exception writeException)
        {
            Debug.LogError($"[Voice Rolloff A/B] Could not write failure report: {writeException}");
        }
        Debug.LogError($"[Voice Rolloff A/B] {message}\nFailure report: {resultPath}");
    }

    private void OnGUI()
    {
        GUI.Label(new Rect(16f, 16f, 1400f, 30f), status);
    }

    private static float Db(float linear)
    {
        return 20f * Mathf.Log10(Mathf.Max(linear, 1e-9f));
    }
}
