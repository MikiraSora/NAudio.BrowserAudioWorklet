using System;
using NAudio.Wave;
using NAudio.Wave.Browser;

namespace NAudio.BrowserAudioWorklet.Tests;

internal class TestSampleProvider : ISampleProvider
{
    private readonly float[] samples;
    private int position;

    public TestSampleProvider(int sampleRate, int channels, params float[] samples)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        this.samples = samples;
    }

    public WaveFormat WaveFormat { get; }

    public float[] LastDestination { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        LastDestination = buffer;
        int available = samples.Length - position;
        int copied = Math.Min(count, Math.Max(0, available));
        samples.AsSpan(position, copied).CopyTo(buffer.AsSpan(offset, copied));
        position += copied;
        return copied;
    }
}

internal sealed class SeekableTestSampleProvider : TestSampleProvider, ISeekableSampleProvider
{
    public SeekableTestSampleProvider(int sampleRate, int channels, TimeSpan duration)
        : base(sampleRate, channels, new float[checked((int)(duration.TotalSeconds * sampleRate * channels))])
    {
        Duration = duration;
    }

    public TimeSpan Position { get; set; }

    public TimeSpan Duration { get; }
}
