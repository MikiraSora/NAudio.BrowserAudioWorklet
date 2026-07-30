using System;
using NAudio.Wave;

namespace NAudio.Avalonia.BrowserAudioWorklet.Tests;

/// <summary>
/// A deterministic 16-bit PCM source that yields a fixed run of samples then end-of-stream.
/// Lets tests assert the exact float values and channel ordering the player pushes to the bridge.
/// </summary>
internal sealed class SequenceWaveProvider : IWaveProvider
{
    private readonly short[] samples;
    private int position;

    public SequenceWaveProvider(WaveFormat waveFormat, short[] samples)
    {
        WaveFormat = waveFormat;
        this.samples = samples;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Number of <c>Read</c> calls made, used to check partial-read handling.</summary>
    public int ReadCallCount { get; private set; }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> buffer)
    {
        ReadCallCount++;
        int bytesPerSample = sizeof(short);
        int samplesToWrite = Math.Min(buffer.Length / bytesPerSample, samples.Length - position);
        for (int i = 0; i < samplesToWrite; i++)
        {
            short value = samples[position++];
            buffer[i * 2] = (byte)(value & 0xFF);
            buffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return samplesToWrite * bytesPerSample;
    }
}
