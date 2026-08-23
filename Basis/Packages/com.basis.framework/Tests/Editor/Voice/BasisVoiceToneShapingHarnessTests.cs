using Basis.Scripts.Networking.Receivers;
using NUnit.Framework;
using System;
using System.Text;
using UnityEngine;

namespace Basis.Tests.Voice
{
    /// <summary>
    /// Diagnostic harness for answering one question: does the optional remote-voice
    /// tone shaper reduce the level of a speech-like signal, and by how much?
    ///
    /// This deliberately excludes distance rolloff, Steam Audio attenuation,
    /// per-player volume and the listener-cone broadband term. The only difference
    /// between baseline and measured output is BasisVoiceToneShaper.Process().
    /// </summary>
    public class BasisVoiceToneShapingHarnessTests
    {
        private static readonly AnimationCurve LegacyRolloff = new AnimationCurve(
            new Keyframe(0.036f, 1f, -2.214f, -2.214f),
            new Keyframe(0.239f, 0.575f, -2.305f, -2.305f),
            new Keyframe(0.372f, 0.328f, -1.068f, -1.068f),
            new Keyframe(0.621f, 0.144f, -0.515f, -0.515f),
            new Keyframe(1f, 0f, -0.031f, -0.031f));

        [TestCase(1f)]
        [TestCase(2f)]
        [TestCase(3f)]
        [TestCase(5f)]
        [TestCase(6f)]
        [TestCase(10f)]
        [TestCase(15f)]
        public void NaturalRolloff_IsMateriallyQuieterThanLegacyAtSocialDistances(float distance)
        {
            const float minDistance = 0.5f;
            const float maxDistance = 25f;

            float natural = BasisVoiceAcoustics.DistanceGain(distance, minDistance, maxDistance);
            float legacy = LegacyRolloff.Evaluate(distance / maxDistance);
            float differenceDb = Db(natural / legacy);

            TestContext.WriteLine($"{distance:F1} m: legacy={legacy:F6}, natural={natural:F6}, natural-vs-legacy={differenceDb:F2} dB");

            Assert.Less(natural, legacy,
                $"Natural should be quieter than Legacy at {distance:F1} m");
        }

        [Test]
        public void NaturalRolloff_ProducesMuchLargerLossThanToneShaping()
        {
            const float minDistance = 0.5f;
            const float maxDistance = 25f;
            const float distance = 5f;

            float natural = BasisVoiceAcoustics.DistanceGain(distance, minDistance, maxDistance);
            float legacy = LegacyRolloff.Evaluate(distance / maxDistance);
            float rolloffLossDb = Db(natural / legacy);

            float worstToneShapingDb = MeasureSpeechLikeToneShapingDb(180f, 180f);

            TestContext.WriteLine($"At {distance:F1} m: Natural-vs-Legacy={rolloffLossDb:F2} dB; worst tone shaping={worstToneShapingDb:F2} dB");

            Assert.Less(rolloffLossDb, -10f,
                "Natural rolloff should account for a double-digit dB reduction at a normal social distance");
            Assert.Greater(Mathf.Abs(rolloffLossDb), Mathf.Abs(worstToneShapingDb) * 4f,
                "Natural rolloff loss should dominate the tone-shaping loss by a wide margin");
        }

        private const int SampleRate = 48000;
        private const int BlockFrames = 1024;
        private const int DurationSeconds = 2;
        private const int WarmupFrames = SampleRate / 4;

        // Long-term-average-speech octave-band weights already used by the acoustic
        // model tests. Amplitude is sqrt(power weight) when synthesising the signal.
        private static readonly float[] SpeechBandsHz =
            { 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f };

        private static readonly float[] SpeechPowerWeights =
            { 0.2262f, 0.5687f, 0.4517f, 0.1429f, 0.0452f, 0.0143f, 0.0029f };

        [Test]
        public void ToneShaping_LoudnessImpactHarness()
        {
            float[] input = BuildSpeechLikeSignal();
            float inputRms = Rms(input, WarmupFrames);

            var report = new StringBuilder();
            report.AppendLine("Remote voice tone-shaping diagnostic (tone shaper only)");
            report.AppendLine("Talker  Listener  DirectivityShelf  HeadShelf  RMS delta");

            float rearRearDeltaDb = 0f;
            foreach ((float talkerDeg, float listenerDeg) in new[]
            {
                (0f, 0f),
                (90f, 0f),
                (180f, 0f),
                (0f, 90f),
                (0f, 180f),
                (90f, 90f),
                (180f, 180f),
            })
            {
                float directivityShelfDb = BasisVoiceAcoustics.DirectivityShelfDb(
                    Mathf.Cos(talkerDeg * Mathf.Deg2Rad));

                BasisVoiceAcoustics.ListenerConeTerms(
                    listenerDeg * Mathf.Deg2Rad,
                    150f,
                    60f,
                    out _,
                    out float headShelfDb);

                float[] output = (float[])input.Clone();
                ProcessInAudioBlocks(output, directivityShelfDb, headShelfDb);
                float deltaDb = Db(Rms(output, WarmupFrames) / inputRms);

                report.AppendLine(
                    $"{talkerDeg,6:F0}°  {listenerDeg,7:F0}°  {directivityShelfDb,16:F2} dB  " +
                    $"{headShelfDb,8:F2} dB  {deltaDb,8:F2} dB");

                if (talkerDeg == 0f && listenerDeg == 0f)
                {
                    Assert.AreEqual(0f, deltaDb, 0.01f,
                        "Facing each other should be an identity; the harness baseline is invalid otherwise.");
                }

                if (talkerDeg == 180f && listenerDeg == 180f)
                {
                    rearRearDeltaDb = deltaDb;
                }
            }

            TestContext.Progress.WriteLine(report.ToString());

            // This is intentionally a broad diagnostic guard, not a fitted-number
            // regression test. It proves that enabling the shaper can lower measured
            // speech RMS by an audible amount without any distance attenuation in play.
            Assert.Less(rearRearDeltaDb, -1f,
                $"Expected an audible tone-shaping loss when both parties face away; measured {rearRearDeltaDb:F2} dB.");
        }

        private static float MeasureSpeechLikeToneShapingDb(float talkerDeg, float listenerDeg)
        {
            float[] input = BuildSpeechLikeSignal();
            float inputRms = Rms(input, WarmupFrames);
            float directivityShelfDb = BasisVoiceAcoustics.DirectivityShelfDb(
                Mathf.Cos(talkerDeg * Mathf.Deg2Rad));
            BasisVoiceAcoustics.ListenerConeTerms(
                listenerDeg * Mathf.Deg2Rad,
                150f,
                60f,
                out _,
                out float headShelfDb);

            float[] output = (float[])input.Clone();
            ProcessInAudioBlocks(output, directivityShelfDb, headShelfDb);
            return Db(Rms(output, WarmupFrames) / inputRms);
        }

        private static float[] BuildSpeechLikeSignal()
        {
            int frames = SampleRate * DurationSeconds;
            var signal = new float[frames];

            // Fixed phases make the harness deterministic while avoiding every band
            // crossing zero at the same instant.
            for (int i = 0; i < frames; i++)
            {
                double t = (double)i / SampleRate;
                double sample = 0.0;
                for (int band = 0; band < SpeechBandsHz.Length; band++)
                {
                    double amplitude = Math.Sqrt(SpeechPowerWeights[band]);
                    double phase = band * 0.731;
                    sample += amplitude * Math.Sin(2.0 * Math.PI * SpeechBandsHz[band] * t + phase);
                }
                signal[i] = (float)sample;
            }

            // Headroom has no effect on the measured ratio; it just keeps this useful
            // if the signal is later written to an AudioClip/WAV for listening tests.
            float peak = 0f;
            for (int i = 0; i < signal.Length; i++)
            {
                peak = Mathf.Max(peak, Mathf.Abs(signal[i]));
            }
            float scale = peak > 0f ? 0.5f / peak : 1f;
            for (int i = 0; i < signal.Length; i++) signal[i] *= scale;
            return signal;
        }

        private static void ProcessInAudioBlocks(float[] samples, float directivityShelfDb, float headShelfDb)
        {
            var shaper = new BasisVoiceToneShaper();
            var block = new float[BlockFrames];

            for (int offset = 0; offset < samples.Length; offset += BlockFrames)
            {
                int frames = Math.Min(BlockFrames, samples.Length - offset);
                Array.Clear(block, 0, block.Length);
                Array.Copy(samples, offset, block, 0, frames);
                shaper.Process(block, 1, frames, SampleRate, directivityShelfDb, headShelfDb);
                Array.Copy(block, 0, samples, offset, frames);
            }
        }

        private static float Rms(float[] samples, int startFrame)
        {
            double sum = 0.0;
            int count = Math.Max(0, samples.Length - startFrame);
            for (int i = startFrame; i < samples.Length; i++)
            {
                sum += (double)samples[i] * samples[i];
            }
            return count > 0 ? (float)Math.Sqrt(sum / count) : 0f;
        }

        private static float Db(float linear)
        {
            return 20f * Mathf.Log10(Mathf.Max(linear, 1e-9f));
        }
    }
}
