using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.TestTools;

namespace Basis.Tests.Voice
{
    public class BasisVoiceRolloffABPlayModeTests
    {
        private const int SampleRate = 48000;
        private const float MinDistance = 0.5f;
        private const float MaxDistance = 25f;
        private const int CaptureSamples = 4096;

        private static readonly AnimationCurve LegacyRolloff = new AnimationCurve(
            new Keyframe(0.036f, 1f, -2.214f, -2.214f),
            new Keyframe(0.239f, 0.575f, -2.305f, -2.305f),
            new Keyframe(0.372f, 0.328f, -1.068f, -1.068f),
            new Keyframe(0.621f, 0.144f, -0.515f, -0.515f),
            new Keyframe(1f, 0f, -0.031f, -0.031f));

        [UnityTest]
        public IEnumerator NaturalVsLegacy_FixedDistanceAudioOutputAB()
        {
            var listenerObject = new GameObject("Voice A/B Listener");
            var sourceObject = new GameObject("Voice A/B Source");
            AudioClip clip = null;
            AudioListener[] existingListeners = UnityEngine.Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None);

            try
            {
                foreach (AudioListener listener in existingListeners)
                {
                    listener.enabled = false;
                }
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

                clip = BuildToneClip();
                source.clip = clip;

                float twoDimensionalRms = 0f;
                source.spatialBlend = 0f;
                yield return MeasureOutput(source, 0f, null, rms => twoDimensionalRms = rms);
                Assert.Greater(twoDimensionalRms, 1e-5f,
                    "Unity audio output is unavailable in this PlayMode environment; the A/B would be invalid.");
                source.spatialBlend = 1f;

                var report = new StringBuilder();
                report.AppendLine("Fixed-distance Unity audio-output A/B");
                report.AppendLine($"2D validation RMS: {twoDimensionalRms:F6}");
                report.AppendLine("distance | legacy RMS | natural RMS | natural/legacy dB");

                foreach (float distance in new[] { 1f, 2f, 5f, 10f })
                {
                    float legacyRms = 0f;
                    float naturalRms = 0f;

                    yield return MeasureOutput(source, distance, LegacyRolloff, rms => legacyRms = rms);
                    yield return MeasureOutput(source, distance,
                        BasisVoiceAcoustics.BuildRolloffCurve(MinDistance, MaxDistance),
                        rms => naturalRms = rms);

                    Assert.Greater(legacyRms, 1e-7f, $"Legacy output was silent at {distance:F1} m.");
                    float differenceDb = Db(naturalRms / legacyRms);
                    report.AppendLine($"{distance,8:F1} | {legacyRms,10:F6} | {naturalRms,11:F6} | {differenceDb,17:F2}");

                    Assert.Less(naturalRms, legacyRms,
                        $"Natural should be quieter than Legacy at {distance:F1} m in actual Unity output.");

                    if (Mathf.Approximately(distance, 5f))
                    {
                        Assert.Less(differenceDb, -10f,
                            $"Expected a double-digit dB Natural-vs-Legacy loss at 5 m; measured {differenceDb:F2} dB.");
                    }
                }

                TestContext.Progress.WriteLine(report.ToString());
            }
            finally
            {
                if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
                UnityEngine.Object.DestroyImmediate(sourceObject);
                UnityEngine.Object.DestroyImmediate(listenerObject);
                foreach (AudioListener listener in existingListeners)
                {
                    if (listener != null) listener.enabled = true;
                }
            }
        }

        private static IEnumerator MeasureOutput(
            AudioSource source,
            float distance,
            AnimationCurve rolloff,
            Action<float> result)
        {
            source.Stop();
            source.transform.position = new Vector3(distance, 0f, 0f);
            if (rolloff != null)
            {
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, rolloff);
            }

            source.Play();
            yield return new WaitForSecondsRealtime(0.25f);

            var samples = new float[CaptureSamples];
            AudioListener.GetOutputData(samples, 0);
            result(Rms(samples));
            source.Stop();
            yield return null;
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

        private static float Rms(float[] samples)
        {
            double sum = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += (double)samples[i] * samples[i];
            }
            return samples.Length > 0 ? (float)Math.Sqrt(sum / samples.Length) : 0f;
        }

        private static float Db(float linear)
        {
            return 20f * Mathf.Log10(Mathf.Max(linear, 1e-9f));
        }
    }
}
