using System;

namespace NAudio.Wave.Browser;

/// <summary>Measured latency values reported by the browser audio context.</summary>
public sealed record BrowserAudioLatencyInfo(
    int SampleRate,
    double BaseLatencySeconds,
    double OutputLatencySeconds,
    int BufferFrameCount)
{
    /// <summary>Processing plus device output latency reported by the browser.</summary>
    public double EstimatedDeviceLatencySeconds => BaseLatencySeconds + OutputLatencySeconds;
}

/// <summary>Transport counters collected by the AudioWorklet processor.</summary>
public sealed record BrowserAudioPlaybackMetrics(
    int UnderrunCount,
    long UnderrunFrameCount,
    double? FirstFrameContextTimeSeconds,
    bool IsFirstFrameRendered,
    double? EstimatedStartToOutputLatencySeconds = null);

/// <summary>Raised when the processor emits its first source frame for a run.</summary>
public sealed class BrowserAudioFirstFrameEventArgs : EventArgs
{
    internal BrowserAudioFirstFrameEventArgs(
        double contextTimeSeconds,
        double estimatedStartToOutputLatencySeconds,
        BrowserAudioLatencyInfo latency)
    {
        ContextTimeSeconds = contextTimeSeconds;
        EstimatedOutputTimeSeconds = contextTimeSeconds + latency.EstimatedDeviceLatencySeconds;
        EstimatedStartToOutputLatencySeconds = estimatedStartToOutputLatencySeconds;
    }

    /// <summary>AudioContext time at the first rendered frame.</summary>
    public double ContextTimeSeconds { get; }

    /// <summary>Estimated context time at the output device after reported latency.</summary>
    public double EstimatedOutputTimeSeconds { get; }

    /// <summary>
    /// Estimated elapsed time from the <see cref="BrowserAudioWorkletPlayer.PlayAsync"/> request
    /// to the first source frame reaching the output device.
    /// </summary>
    public double EstimatedStartToOutputLatencySeconds { get; }
}

/// <summary>Raised when the processor recovers from an output buffer underrun.</summary>
public sealed class BrowserAudioUnderrunEventArgs : EventArgs
{
    internal BrowserAudioUnderrunEventArgs(long missingFrames)
    {
        MissingFrames = missingFrames;
    }

    /// <summary>Frames that were output as silence during the underrun.</summary>
    public long MissingFrames { get; }
}
