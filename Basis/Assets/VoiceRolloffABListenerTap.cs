using System;
using UnityEngine;

public sealed class VoiceRolloffABListenerTap : MonoBehaviour
{
    private readonly object sync = new object();
    private volatile bool capturing;
    private double sumSquares;
    private long sampleFrames;
    private int callbackCount;
    private int channels;

    public long SampleFrames
    {
        get
        {
            lock (sync)
            {
                return sampleFrames;
            }
        }
    }

    public int CallbackCount
    {
        get
        {
            lock (sync)
            {
                return callbackCount;
            }
        }
    }

    public void BeginCapture()
    {
        lock (sync)
        {
            sumSquares = 0.0;
            sampleFrames = 0;
            callbackCount = 0;
            channels = 0;
            capturing = true;
        }
    }

    public float EndCapture(out long capturedFrames, out int capturedChannels, out int callbacks)
    {
        lock (sync)
        {
            capturing = false;
            capturedFrames = sampleFrames;
            capturedChannels = channels;
            callbacks = callbackCount;
            long scalarSamples = sampleFrames * Math.Max(channels, 1);
            return scalarSamples > 0 ? (float)Math.Sqrt(sumSquares / scalarSamples) : 0f;
        }
    }

    private void OnAudioFilterRead(float[] data, int channelCount)
    {
        if (!capturing)
        {
            return;
        }

        double localSumSquares = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            float sample = data[i];
            localSumSquares += (double)sample * sample;
        }

        lock (sync)
        {
            if (!capturing)
            {
                return;
            }

            sumSquares += localSumSquares;
            channels = channelCount;
            sampleFrames += channelCount > 0 ? data.Length / channelCount : 0;
            callbackCount++;
        }
    }
}
