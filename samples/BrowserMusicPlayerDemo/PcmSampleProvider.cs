using NAudio.Wave;
using NAudio.Wave.Browser;

namespace BrowserMusicPlayerDemo;

/// <summary>
/// A seekable <see cref="ISampleProvider"/> over a fully decoded interleaved float buffer.
/// Position is tracked in samples so it always stays aligned to whole frames.
/// </summary>
internal sealed class PcmSampleProvider : ISeekableSampleProvider
{
    private readonly float[] samples;
    private long position;

    public PcmSampleProvider(DecodedAudio audio)
    {
        samples = audio.Samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(audio.SampleRate, audio.Channels);
        LengthFrames = audio.Frames;
    }

    public WaveFormat WaveFormat { get; }

    public long LengthFrames { get; }

    public long PositionFrames
    {
        get => position / WaveFormat.Channels;
        set => position = Math.Clamp(value, 0, LengthFrames) * WaveFormat.Channels;
    }

    public TimeSpan Position
    {
        get => TimeSpan.FromSeconds(PositionFrames / (double)WaveFormat.SampleRate);
        set => PositionFrames = (long)Math.Round(value.TotalSeconds * WaveFormat.SampleRate);
    }

    public TimeSpan Duration
        => TimeSpan.FromSeconds(LengthFrames / (double)WaveFormat.SampleRate);

    public int Read(float[] buffer, int offset, int count)
    {
        long available = samples.Length - position;
        int toCopy = (int)Math.Min(count, Math.Max(0, available));

        // The sample-provider overload passes the bridge-owned float[] directly, so Span.CopyTo
        // can use the WebAssembly runtime's optimized bulk-memory path.
        samples.AsSpan(checked((int)position), toCopy).CopyTo(buffer.AsSpan(offset, toCopy));

        position += toCopy;
        return toCopy;
    }
}
