using System;

namespace NAudio.Wave.Browser;

/// <summary>Latency-oriented presets for <see cref="BrowserAudioWorkletPlayer"/>.</summary>
public enum BrowserAudioLatencyProfile
{
    /// <summary>Small buffering for interactive effects and instruments.</summary>
    Interactive,

    /// <summary>A compromise between responsiveness and tolerance of UI stalls.</summary>
    Balanced,

    /// <summary>Larger buffering for uninterrupted music playback.</summary>
    Playback,
}

/// <summary>Options controlling the browser audio transport.</summary>
public sealed class BrowserAudioWorkletOptions
{
    /// <summary>Target amount of audio kept ahead of the audio thread.</summary>
    public int BufferDurationMilliseconds { get; init; } = 250;

    /// <summary>
    /// Number of frames requested for the first transfer. The remainder is filled in the
    /// background after the first block is available.
    /// </summary>
    public int InitialBufferFrameCount { get; init; } = 512;

    /// <summary>
    /// When true, the browser chooses the output device's native sample rate and the managed
    /// source is resampled when necessary. This avoids an additional browser output resampler.
    /// </summary>
    public bool UseDeviceSampleRate { get; init; } = true;

    /// <summary>Creates options for a latency profile.</summary>
    public static BrowserAudioWorkletOptions ForProfile(BrowserAudioLatencyProfile profile)
        => profile switch
        {
            BrowserAudioLatencyProfile.Interactive => new BrowserAudioWorkletOptions
            {
                BufferDurationMilliseconds = 20,
                InitialBufferFrameCount = 512,
                UseDeviceSampleRate = true,
            },
            BrowserAudioLatencyProfile.Balanced => new BrowserAudioWorkletOptions
            {
                BufferDurationMilliseconds = 80,
                InitialBufferFrameCount = 512,
                UseDeviceSampleRate = true,
            },
            BrowserAudioLatencyProfile.Playback => new BrowserAudioWorkletOptions
            {
                BufferDurationMilliseconds = 250,
                InitialBufferFrameCount = 512,
                UseDeviceSampleRate = true,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown latency profile."),
        };
}
