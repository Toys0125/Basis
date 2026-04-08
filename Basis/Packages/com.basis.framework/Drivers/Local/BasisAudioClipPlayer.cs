#if UNITY_SERVER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Basis.Network.Core;
using Basis.Scripts.Networking;
using OpusSharp.Core;
using UnityEngine;
using UnityEngine.Networking;
using static SerializableBasis;

/// <summary>
/// Headless audio clip player for stress testing. Loads audio files from a directory,
/// picks one randomly, Opus-encodes it, and sends it over the network as voice audio.
/// Self-contained: has its own Opus encoder and sends directly via the network peer.
///
/// Place .wav or .mp3 files in: {Application.dataPath}/AudioClips/
/// If the directory is missing or empty, no audio is sent (silent headless as usual).
///
/// Designed for testing what 1000+ simultaneous audio sources sound and look like.
/// Each headless client picks a random clip and loops it over the network.
/// </summary>
public static class BasisAudioClipPlayer
{
    public static bool IsActive { get; private set; }

    private static float[] clipSamples;
    private static int clipPosition;
    private static Thread playbackThread;
    private static volatile bool shouldRun;

    private static OpusEncoder encoder;
    private static AudioSegmentDataMessage segment;
    private static NetDataWriter writer;
    private static byte sequenceNumber;
    private static int initializationVersion;

    private const int SampleRate = 48000;
    private const int Channels = 1;
    private const float FrameDurationSeconds = 0.02f; // 20ms
    private static readonly int FrameSize = (int)(FrameDurationSeconds * SampleRate); // 960
    private static readonly string[] SupportedExtensions = { ".wav", ".mp3" };

    /// <summary>
    /// Directory to scan for supported audio files. Defaults to {Application.dataPath}/AudioClips/
    /// </summary>
    public static string ClipDirectory;

    /// <summary>
    /// Attempts to initialize the clip player. If the AudioClips directory exists and
    /// contains supported audio files, a random clip is loaded and streamed as voice audio.
    /// If the directory is missing or empty, this is a no-op (silent headless as usual).
    /// </summary>
    public static async Task<bool> TryInitializeAsync()
    {
        if (IsActive)
        {
            return true;
        }

        int initVersion = Interlocked.Increment(ref initializationVersion);

        string dir = ClipDirectory ?? Path.Combine(Application.dataPath, "AudioClips");
        BasisDebug.Log($"[AudioClipPlayer] Booting up. AudioClips directory: {dir}", BasisDebug.LogTag.Device);

        if (!Directory.Exists(dir))
        {
            try
            {
                Directory.CreateDirectory(dir);
                BasisDebug.Log($"[AudioClipPlayer] Created AudioClips directory: {dir}", BasisDebug.LogTag.Device);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[AudioClipPlayer] Failed to create AudioClips directory: {dir} - {ex.Message}", BasisDebug.LogTag.Device);
            }
            return false;
        }

        List<string> files = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string extension = Path.GetExtension(file);
            for (int i = 0; i < SupportedExtensions.Length; i++)
            {
                if (extension.Equals(SupportedExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                    break;
                }
            }
        }

        if (files.Count == 0)
        {
            BasisDebug.LogError("[AudioClipPlayer] Failed to find any supported audio clips (.wav, .mp3).", BasisDebug.LogTag.Device);
            return false;
        }

        string chosen = files[UnityEngine.Random.Range(0, files.Count)];
        BasisDebug.Log($"[AudioClipPlayer] Loading: {Path.GetFileName(chosen)}", BasisDebug.LogTag.Device);

        clipSamples = await LoadAudioFileAsMono48kAsync(chosen);
        if (clipSamples == null || clipSamples.Length == 0)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Failed to load: {chosen}", BasisDebug.LogTag.Device);
            return false;
        }

        if (initVersion != Volatile.Read(ref initializationVersion))
        {
            BasisDebug.LogWarning("[AudioClipPlayer] Ignoring stale audio clip initialization request.", BasisDebug.LogTag.Device);
            clipSamples = null;
            return false;
        }

        // Initialize Opus encoder
        encoder = new OpusEncoder(SampleRate, Channels, OpusPredefinedValues.OPUS_APPLICATION_AUDIO, use_static: false);
        encoder.Ctl(EncoderCTL.OPUS_SET_BITRATE, 32000);
        encoder.Ctl(EncoderCTL.OPUS_SET_COMPLEXITY, 5);

        // Initialize send buffers
        int packetSize = FrameSize * 4;
        segment = new AudioSegmentDataMessage
        {
            buffer = new byte[packetSize],
            TotalLength = packetSize
        };
        writer = new NetDataWriter();
        sequenceNumber = 0;

        clipPosition = 0;
        shouldRun = true;
        IsActive = true;

        playbackThread = new Thread(PlaybackLoop)
        {
            IsBackground = true,
            Name = "HeadlessAudioClipPlayer"
        };
        playbackThread.Start();

        BasisDebug.Log($"[AudioClipPlayer] Active: {Path.GetFileName(chosen)} ({clipSamples.Length} samples, looping at {SampleRate}Hz)", BasisDebug.LogTag.Device);
        return true;
    }

    /// <summary>
    /// Stop the clip player and clean up resources.
    /// </summary>
    public static void DeInitialize()
    {
        Interlocked.Increment(ref initializationVersion);
        shouldRun = false;
        IsActive = false;

        if (playbackThread != null && playbackThread.IsAlive)
        {
            playbackThread.Join(500);
        }
        playbackThread = null;
        clipSamples = null;
        clipPosition = 0;

        encoder?.Dispose();
        encoder = null;
    }

    /// <summary>
    /// Background thread that encodes one audio frame (20ms / 960 samples) with Opus
    /// and sends it directly over the network as voice data every interval.
    /// Waits for the network peer to be available before sending.
    /// </summary>
    private static void PlaybackLoop()
    {
        BasisDebug.Log("[AudioClipPlayer] Playback thread started", BasisDebug.LogTag.Device);
        try
        {
            long intervalTicks = (long)(FrameDurationSeconds * System.Diagnostics.Stopwatch.Frequency);
            float[] frameBuffer = new float[FrameSize];
            bool loggedFirstSend = false;
            int peerNullCount = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextFrameTick = sw.ElapsedTicks;

            while (shouldRun)
            {
                // Sleep until the next frame boundary, compensating for encode/send time
                long now = sw.ElapsedTicks;
                long waitTicks = nextFrameTick - now;
                if (waitTicks > 0)
                {
                    int waitMs = (int)(waitTicks * 1000 / System.Diagnostics.Stopwatch.Frequency);
                    if (waitMs > 0)
                        Thread.Sleep(waitMs);
                }
                nextFrameTick += intervalTicks;

                // If we fell behind by more than 5 frames, reset to avoid burst-sending
                if (sw.ElapsedTicks - nextFrameTick > intervalTicks * 5)
                    nextFrameTick = sw.ElapsedTicks;

                if (!shouldRun || clipSamples == null || encoder == null)
                    break;

                // Fill frame from clip (looping)
                for (int i = 0; i < FrameSize; i++)
                {
                    frameBuffer[i] = clipSamples[clipPosition];
                    clipPosition++;
                    if (clipPosition >= clipSamples.Length)
                    {
                        clipPosition = 0;
                    }
                }

                NetPeer peer = BasisNetworkConnection.LocalPlayerPeer;
                if (peer == null)
                {
                    peerNullCount++;
                    if (peerNullCount % 250 == 1)
                    {
                        BasisDebug.LogWarning($"[AudioClipPlayer] Waiting for network peer... ({peerNullCount} frames skipped)", BasisDebug.LogTag.Device);
                    }

                    continue;
                }

                // Encode with Opus
                segment.LengthUsed = encoder.Encode(frameBuffer, FrameSize, segment.buffer, segment.TotalLength);
                segment.SequenceNumber = sequenceNumber++;
                segment.TotalPlayedInSilence = 0;

                if (!loggedFirstSend)
                {
                    loggedFirstSend = true;
                    BasisDebug.Log($"[AudioClipPlayer] First packet sent. Encoded {segment.LengthUsed} bytes, seq={segment.SequenceNumber}, peer={peer.Id}", BasisDebug.LogTag.Device);
                }

                // Send on voice channel
                writer.Reset();
                segment.Serialize(writer);
                peer.Send(writer, BasisNetworkCommons.VoiceChannel, DeliveryMethod.Unreliable);
            }

            BasisDebug.Log("[AudioClipPlayer] Playback thread exiting normally", BasisDebug.LogTag.Device);
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Playback thread crashed: {ex}", BasisDebug.LogTag.Device);
        }
    }

    private static async Task<float[]> LoadAudioFileAsMono48kAsync(string path)
    {
        AudioType audioType = GetAudioType(path);
        if (audioType == AudioType.UNKNOWN)
        {
            BasisDebug.LogWarning($"[AudioClipPlayer] Unsupported audio type: {Path.GetExtension(path)}", BasisDebug.LogTag.Device);
            return null;
        }

        string fileUri = new Uri(path).AbsoluteUri;
        AudioClip clip = null;

        using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(fileUri, audioType))
        {
            try
            {
                // Keep DownloadHandlerAudioClip in its default non-streaming mode because this path
                // needs AudioClip.GetData() to extract PCM for the Opus encoder.
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                await AwaitAsyncOperation(operation);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    BasisDebug.LogError($"[AudioClipPlayer] Audio load failed for {path}: {request.error}", BasisDebug.LogTag.Device);
                    return null;
                }

                clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    BasisDebug.LogError($"[AudioClipPlayer] UnityWebRequest returned a null AudioClip for {path}", BasisDebug.LogTag.Device);
                    return null;
                }

                if (clip.samples <= 0 || clip.channels <= 0)
                {
                    BasisDebug.LogError($"[AudioClipPlayer] Loaded clip has invalid sample metadata: {path}", BasisDebug.LogTag.Device);
                    return null;
                }

                int sampleCount = clip.samples * clip.channels;
                float[] interleavedSamples = new float[sampleCount];
                if (!clip.GetData(interleavedSamples, 0))
                {
                    BasisDebug.LogError($"[AudioClipPlayer] Failed to read sample data from {path}", BasisDebug.LogTag.Device);
                    return null;
                }

                BasisDebug.Log($"[AudioClipPlayer] Loaded clip metadata: {clip.frequency}Hz, {clip.channels}ch, {clip.samples} frames", BasisDebug.LogTag.Device);
                return ConvertToMono48k(interleavedSamples, clip.channels, clip.frequency);
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[AudioClipPlayer] Audio load error: {ex.Message}", BasisDebug.LogTag.Device);
                return null;
            }
            finally
            {
                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }
            }
        }
    }

    private static AudioType GetAudioType(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return AudioType.WAV;
        }

        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return AudioType.MPEG;
        }

        return AudioType.UNKNOWN;
    }

    private static Task AwaitAsyncOperation(AsyncOperation operation)
    {
        if (operation.isDone)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.completed += _ => tcs.TrySetResult(true);
        return tcs.Task;
    }

    private static float[] ConvertToMono48k(float[] interleavedSamples, int channels, int sampleRate)
    {
        try
        {
            int totalFrames = interleavedSamples.Length / channels;
            float[] monoSamples = channels == 1 ? interleavedSamples : new float[totalFrames];

            if (channels > 1)
            {
                for (int i = 0; i < totalFrames; i++)
                {
                    float sum = 0f;
                    int baseIndex = i * channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += interleavedSamples[baseIndex + ch];
                    }

                    monoSamples[i] = sum / channels;
                }
            }

            if (sampleRate != SampleRate)
            {
                BasisDebug.Log($"[AudioClipPlayer] Resampling from {sampleRate}Hz to {SampleRate}Hz", BasisDebug.LogTag.Device);
                monoSamples = Resample(monoSamples, sampleRate, SampleRate);
            }

            return monoSamples;
        }
        catch (Exception ex)
        {
            BasisDebug.LogError($"[AudioClipPlayer] Sample conversion error: {ex.Message}", BasisDebug.LogTag.Device);
            return null;
        }
    }

    private static float[] Resample(float[] source, int sourceSampleRate, int targetSampleRate)
    {
        double ratio = (double)sourceSampleRate / targetSampleRate;
        int targetLength = (int)(source.Length / ratio);
        float[] result = new float[targetLength];

        for (int i = 0; i < targetLength; i++)
        {
            double srcPos = i * ratio;
            int srcIndex = (int)srcPos;
            double frac = srcPos - srcIndex;

            float s0 = source[Mathf.Min(srcIndex, source.Length - 1)];
            float s1 = source[Mathf.Min(srcIndex + 1, source.Length - 1)];
            result[i] = (float)(s0 + (s1 - s0) * frac);
        }

        return result;
    }
}
#endif
