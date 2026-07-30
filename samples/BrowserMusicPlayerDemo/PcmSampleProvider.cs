using NAudio.Wave;

namespace BrowserMusicPlayerDemo;

/// <summary>
/// A seekable <see cref="ISampleProvider"/> over a fully decoded interleaved float buffer.
/// Position is tracked in samples so it always stays aligned to whole frames.
/// </summary>
internal sealed class PcmSampleProvider : ISampleProvider
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

    public int Read(float[] buffer, int offset, int count)
    {
        long available = samples.Length - position;
        int toCopy = (int)Math.Min(count, Math.Max(0, available));

        // Manual copy instead of Array.Copy: through the player's IWaveProvider chain this
        // buffer can be a WaveBuffer reinterpretation whose runtime array type does not
        // match float[], and Array.Copy rejects that on WebAssembly. Element writes do not.
        for (int i = 0; i < toCopy; i++)
        {
            buffer[offset + i] = samples[position + i];
        }

        position += toCopy;
        return toCopy;
    }
}
