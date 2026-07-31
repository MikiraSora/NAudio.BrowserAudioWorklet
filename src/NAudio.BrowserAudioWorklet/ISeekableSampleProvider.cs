using System;
using NAudio.Wave;

namespace NAudio.Wave.Browser;

/// <summary>
/// Optional sample-provider contract used by <see cref="BrowserAudioWorkletPlayer.SeekAsync"/>.
/// </summary>
public interface ISeekableSampleProvider : ISampleProvider
{
    /// <summary>Current position in the source.</summary>
    TimeSpan Position { get; set; }

    /// <summary>Total source duration.</summary>
    TimeSpan Duration { get; }
}
