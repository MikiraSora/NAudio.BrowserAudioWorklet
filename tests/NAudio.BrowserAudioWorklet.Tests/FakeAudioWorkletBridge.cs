using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave.Browser;

namespace NAudio.BrowserAudioWorklet.Tests;

/// <summary>
/// Deterministic stand-in for the JavaScript <see cref="IAudioWorkletBridge"/>. Records control
/// calls and hands the test direct control over the render callback and stop signal, so the
/// player's state machine and format conversion can be verified with no browser or JS runtime.
/// </summary>
internal sealed class FakeAudioWorkletBridge : IAudioWorkletBridge
{
    private AudioRenderCallback renderFrames;
    private Action<Exception> onStopped;
    private Action<AudioWorkletEvent> onEvent;
    private readonly List<Action<Exception>> stoppedCallbacks = new();
    private readonly List<Action<AudioWorkletEvent>> eventCallbacks = new();
    private long totalConsumedFrameCount;

    public int PrepareCount { get; private set; }
    public int StartCount { get; private set; }
    public int FlushCount { get; private set; }
    public int PauseCount { get; private set; }
    public int ResumeCount { get; private set; }
    public int StopCount { get; private set; }
    public int ResetTotalConsumedCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BufferFrameCount { get; private set; }
    public int InitialBufferFrameCount { get; private set; }
    public int RequestedSampleRate { get; private set; }
    public double RequestLeadTimeSeconds { get; private set; }
    public bool UseDeviceSampleRate { get; private set; }
    public float LastVolume { get; private set; } = float.NaN;
    public List<float> VolumeHistory { get; } = new();
    public bool IsStarted { get; private set; }
    public long TotalConsumedFrameCount => totalConsumedFrameCount;

    /// <summary>Set to force <see cref="StartAsync"/> to fault, simulating a graph build failure.</summary>
    public Exception StartException { get; set; }
    public Exception PrepareException { get; set; }
    public Exception PauseException { get; set; }
    public Exception ResumeException { get; set; }
    public Exception StopException { get; set; }
    public Exception ResetTotalConsumedException { get; set; }
    public TaskCompletionSource<AudioWorkletPreparation> PrepareCompletion { get; set; }
    public TaskCompletionSource StartCompletion { get; set; }
    public int PreparedSampleRate { get; set; }
    public double BaseLatencySeconds { get; set; } = 0.005;
    public double OutputLatencySeconds { get; set; } = 0.01;
    public BrowserAudioPlaybackMetrics Metrics { get; set; } = new(0, 0, null, false);

    public Task<AudioWorkletPreparation> PrepareAsync(
        int requestedSampleRate,
        int channels,
        bool useDeviceSampleRate)
    {
        PrepareCount++;
        RequestedSampleRate = requestedSampleRate;
        Channels = channels;
        UseDeviceSampleRate = useDeviceSampleRate;
        if (PrepareException != null)
        {
            return Task.FromException<AudioWorkletPreparation>(PrepareException);
        }

        if (PrepareCompletion != null)
        {
            return PrepareCompletion.Task;
        }

        SampleRate = PreparedSampleRate == 0 ? requestedSampleRate : PreparedSampleRate;
        return Task.FromResult(new AudioWorkletPreparation(
            SampleRate,
            BaseLatencySeconds,
            OutputLatencySeconds));
    }

    public async Task StartAsync(
        int channels,
        int bufferFrameCount,
        int initialBufferFrameCount,
        double requestLeadTimeSeconds,
        AudioRenderCallback renderFrames,
        Action<Exception> onStopped,
        Action<AudioWorkletEvent> onEvent)
    {
        StartCount++;
        Channels = channels;
        BufferFrameCount = bufferFrameCount;
        InitialBufferFrameCount = initialBufferFrameCount;
        RequestLeadTimeSeconds = requestLeadTimeSeconds;
        this.renderFrames = renderFrames;
        this.onStopped = onStopped;
        this.onEvent = onEvent;
        stoppedCallbacks.Add(onStopped);
        eventCallbacks.Add(onEvent);

        if (StartException != null)
        {
            throw StartException;
        }

        if (StartCompletion != null)
        {
            await StartCompletion.Task;
        }

        IsStarted = true;
    }

    public Task FlushAsync()
    {
        FlushCount++;
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        PauseCount++;
        return PauseException is null ? Task.CompletedTask : Task.FromException(PauseException);
    }

    public Task ResumeAsync()
    {
        ResumeCount++;
        return ResumeException is null ? Task.CompletedTask : Task.FromException(ResumeException);
    }

    public void SetVolume(float volume)
    {
        LastVolume = volume;
        VolumeHistory.Add(volume);
    }

    public Task StopAsync()
    {
        StopCount++;
        IsStarted = false;
        return StopException is null ? Task.CompletedTask : Task.FromException(StopException);
    }

    public Task ResetTotalConsumedAsync()
    {
        ResetTotalConsumedCount++;
        if (ResetTotalConsumedException != null)
        {
            return Task.FromException(ResetTotalConsumedException);
        }

        totalConsumedFrameCount = 0;
        return Task.CompletedTask;
    }

    public void Dispose() => DisposeCount++;

    public Task<BrowserAudioPlaybackMetrics> GetMetricsAsync() => Task.FromResult(Metrics);

    // --- Test driving surface -------------------------------------------------

    /// <summary>Invokes the player's render callback exactly as the real feed loop would.</summary>
    public int Render(float[] destination, int frameCount) => renderFrames(destination, frameCount);

    public int Render(Span<byte> destination, int frameCount)
    {
        var samples = new float[destination.Length / sizeof(float)];
        int frames = renderFrames(samples, frameCount);
        int sampleCount = frames * Channels;
        MemoryMarshal.AsBytes(samples.AsSpan(0, sampleCount)).CopyTo(destination);
        return frames;
    }

    /// <summary>Publishes the exact audio-thread value observed by the player properties.</summary>
    public void SetTotalConsumedFrameCount(long frameCount)
    {
        if (frameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        totalConsumedFrameCount = frameCount;
    }

    /// <summary>Simulates the graph stopping on its own (end of stream or error).</summary>
    public void RaiseStopped(Exception error = null) => onStopped?.Invoke(error);

    public void RaiseEvent(AudioWorkletEvent workletEvent) => onEvent?.Invoke(workletEvent);

    /// <summary>Raises the captured callback for an earlier or current playback run.</summary>
    public void RaiseStoppedForRun(int runIndex, Exception error = null)
        => stoppedCallbacks[runIndex]?.Invoke(error);

    public void RaiseEventForRun(int runIndex, AudioWorkletEvent workletEvent)
        => eventCallbacks[runIndex]?.Invoke(workletEvent);

    public bool HasRenderCallback => renderFrames != null;
    public bool HasStoppedCallback => onStopped != null;
}
