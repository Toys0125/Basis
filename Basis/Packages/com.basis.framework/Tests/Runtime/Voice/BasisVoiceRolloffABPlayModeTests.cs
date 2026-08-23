using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using System;
using System.Collections;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Voice
{
    public class BasisVoiceRolloffABPlayModeTests
    {
        private const int SampleRate = 48000;
        private const float MinDistance = 0.5f;
        private const float MaxDistance = 25f;
        private const int CaptureFramerate = 60;
        private const int WarmupCaptureFrames = 4;
        private const int MinimumCaptureSampleFrames = SampleRate / 4;
        private const int MaximumEmptyCaptureFrames = 120;

        private static readonly AnimationCurve LegacyRolloff = new AnimationCurve(
            new Keyframe(0.036f, 1f, -2.214f, -2.214f),
            new Keyframe(0.239f, 0.575f, -2.305f, -2.305f),
            new Keyframe(0.372f, 0.328f, -1.068f, -1.068f),
            new Keyframe(0.621f, 0.144f, -0.515f, -0.515f),
            new Keyframe(1f, 0f, -0.031f, -0.031f));

        [UnityTest]
        public IEnumerator NaturalVsLegacy_FixedDistanceAudioOutputAB()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("AudioRenderer end-of-frame capture requires an interactive Unity player loop; run this A/B from the Editor Test Runner.");
            }

            AudioListener[] existingListeners = UnityEngine.Object.FindObjectsByType<AudioListener>();
            AudioSource[] existingSources = UnityEngine.Object.FindObjectsByType<AudioSource>();
            bool[] listenerEnabled = CaptureEnabled(existingListeners);
            bool[] sourceEnabled = CaptureEnabled(existingSources);
            bool previousListenerPause = AudioListener.pause;
            float previousListenerVolume = AudioListener.volume;
            int previousCaptureFramerate = Time.captureFramerate;

            var listenerObject = new GameObject("Voice A/B Listener");
            var sourceObject = new GameObject("Voice A/B Source");
            AudioClip clip = null;
            bool rendererStarted = false;

            try
            {
                SetEnabled(existingListeners, false);
                SetEnabled(existingSources, false);
                AudioListener.pause = false;
                AudioListener.volume = 1f;
                Time.captureFramerate = CaptureFramerate;

                listenerObject.transform.position = Vector3.zero;
                listenerObject.AddComponent<AudioListener>();

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
                source.bypassListenerEffects = true;
                source.bypassReverbZones = true;

                clip = BuildToneClip();
                source.clip = clip;

                int channelCount = SpeakerChannelCount(AudioSettings.speakerMode);
                Assert.Greater(channelCount, 0, $"Unsupported speaker mode {AudioSettings.speakerMode}.");

                rendererStarted = AudioRenderer.Start();
                Assert.IsTrue(rendererStarted,
                    "AudioRenderer was already recording; the A/B requires exclusive main-output capture.");

                float twoDimensionalRms = 0f;
                source.spatialBlend = 0f;
                yield return MeasureRenderedOutput(source, "2D control", 0f, null, channelCount, rms => twoDimensionalRms = rms);
                Assert.Greater(twoDimensionalRms, 1e-5f,
                    "AudioRenderer captured silence for the 2D control signal; the A/B would be invalid.");
                source.spatialBlend = 1f;

                var naturalRolloff = BasisVoiceAcoustics.BuildRolloffCurve(MinDistance, MaxDistance);
                var report = new StringBuilder();
                report.AppendLine("Fixed-distance Unity main-output A/B (AudioRenderer)");
                report.AppendLine($"Speaker mode: {AudioSettings.speakerMode} ({channelCount} channels)");
                report.AppendLine($"Capture framerate: {CaptureFramerate} fps");
                report.AppendLine($"2D validation RMS: {twoDimensionalRms:F6}");
                report.AppendLine("distance | legacy RMS | natural RMS | natural/legacy dB");

                foreach (float distance in new[] { 1f, 2f, 5f, 10f })
                {
                    float legacyRms = 0f;
                    float naturalRms = 0f;

                    yield return MeasureRenderedOutput(source, $"Legacy {distance:F1} m", distance, LegacyRolloff, channelCount, rms => legacyRms = rms);
                    yield return MeasureRenderedOutput(source, $"Natural {distance:F1} m", distance, naturalRolloff, channelCount, rms => naturalRms = rms);

                    Assert.Greater(legacyRms, 1e-7f, $"Legacy output was silent at {distance:F1} m.");
                    float differenceDb = Db(naturalRms / legacyRms);
                    report.AppendLine($"{distance,8:F1} | {legacyRms,10:F6} | {naturalRms,11:F6} | {differenceDb,17:F2}");

                    Assert.Less(naturalRms, legacyRms,
                        $"Natural should be quieter than Legacy at {distance:F1} m in Unity main output.");

                    if (Mathf.Approximately(distance, 5f))
                    {
                        Assert.Less(differenceDb, -10f,
                            $"Expected a double-digit dB Natural-vs-Legacy loss at 5 m; measured {differenceDb:F2} dB.");
                    }
                }

                string text = report.ToString();
                TestContext.Progress.WriteLine(text);
                Debug.Log(text);
            }
            finally
            {
                if (rendererStarted)
                {
                    AudioRenderer.Stop();
                }

                if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(sourceObject);
                UnityEngine.Object.DestroyImmediate(listenerObject);

                Time.captureFramerate = previousCaptureFramerate;
                AudioListener.pause = previousListenerPause;
                AudioListener.volume = previousListenerVolume;
                RestoreEnabled(existingSources, sourceEnabled);
                RestoreEnabled(existingListeners, listenerEnabled);
            }
        }

        private static IEnumerator MeasureRenderedOutput(
            AudioSource source,
            string label,
            float distance,
            AnimationCurve rolloff,
            int channelCount,
            Action<float> result)
        {
            source.Stop();
            source.transform.position = new Vector3(distance, 0f, 0f);
            source.timeSamples = 0;
            if (rolloff != null)
            {
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, rolloff);
            }

            source.Play();

            int warmupFrames = 0;
            int emptyFrames = 0;
            while (warmupFrames < WarmupCaptureFrames)
            {
                yield return new WaitForEndOfFrame();
                int sampleFrames = AudioRenderer.GetSampleCountForCaptureFrame();
                if (sampleFrames <= 0)
                {
                    Assert.Less(++emptyFrames, MaximumEmptyCaptureFrames,
                        $"AudioRenderer never produced samples during warmup for {label}.");
                    continue;
                }

                using var discard = new NativeArray<float>(sampleFrames * channelCount, Allocator.Temp);
                Assert.IsTrue(AudioRenderer.Render(discard), "AudioRenderer failed during warmup.");
                warmupFrames++;
            }

            double sumSquares = 0.0;
            long capturedSamples = 0;
            emptyFrames = 0;
            long targetSamples = (long)MinimumCaptureSampleFrames * channelCount;

            while (capturedSamples < targetSamples)
            {
                yield return new WaitForEndOfFrame();
                int sampleFrames = AudioRenderer.GetSampleCountForCaptureFrame();
                if (sampleFrames <= 0)
                {
                    Assert.Less(++emptyFrames, MaximumEmptyCaptureFrames,
                        $"AudioRenderer stopped producing samples during measurement for {label}.");
                    continue;
                }

                using var buffer = new NativeArray<float>(sampleFrames * channelCount, Allocator.Temp);
                Assert.IsTrue(AudioRenderer.Render(buffer), "AudioRenderer failed during measurement.");
                for (int i = 0; i < buffer.Length; i++)
                {
                    float sample = buffer[i];
                    sumSquares += (double)sample * sample;
                }
                capturedSamples += buffer.Length;
            }

            result(capturedSamples > 0 ? (float)Math.Sqrt(sumSquares / capturedSamples) : 0f);
            source.Stop();
        }

        private static AudioClip BuildToneClip()
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

            AudioClip clip = AudioClip.Create("Voice A/B deterministic signal", frames, 1, SampleRate, false);
            Assert.IsTrue(clip.SetData(samples, 0));
            return clip;
        }

        private static int SpeakerChannelCount(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Stereo: return 2;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                case AudioSpeakerMode.Prologic: return 2;
                default: return 0;
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

        private static float Db(float linear)
        {
            return 20f * Mathf.Log10(Mathf.Max(linear, 1e-9f));
        }
    }
}
