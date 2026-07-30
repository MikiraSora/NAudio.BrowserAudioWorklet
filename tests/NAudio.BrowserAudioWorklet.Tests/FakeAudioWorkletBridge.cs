using System;
using System.Collections.Generic;
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
    private readonly List<Action<Exception>> stoppedCallbacks = new();

    public int StartCount { get; private set; }
    public int PauseCount { get; private set; }
    public int ResumeCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BufferFrameCount { get; private set; }
    public float LastVolume { get; private set; } = float.NaN;
    public List<float> VolumeHistory { get; } = new();
    public bool IsStarted { get; private set; }

    /// <summary>Set to force <see cref="StartAsync"/> to fault, simulating a graph build failure.</summary>
    public Exception StartException { get; set; }
    public Exception PauseException { get; set; }
    public Exception ResumeException { get; set; }
    public Exception StopException { get; set; }
    public TaskCompletionSource StartCompletion { get; set; }

    public async Task StartAsync(
        int sampleRate,
        int channels,
        int bufferFrameCount,
        AudioRenderCallback renderFrames,
        Action<Exception> onStopped)
    {
        StartCount++;
        SampleRate = sampleRate;
        Channels = channels;
        BufferFrameCount = bufferFrameCount;
        this.renderFrames = renderFrames;
        this.onStopped = onStopped;
        stoppedCallbacks.Add(onStopped);

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

    public void Dispose() => DisposeCount++;

    // --- Test driving surface -------------------------------------------------

    /// <summary>Invokes the player's render callback exactly as the real feed loop would.</summary>
    public int Render(Span<byte> destination, int frameCount) => renderFrames(destination, frameCount);

    /// <summary>Simulates the graph stopping on its own (end of stream or error).</summary>
    public void RaiseStopped(Exception error = null) => onStopped?.Invoke(error);

    /// <summary>Raises the captured callback for an earlier or current playback run.</summary>
    public void RaiseStoppedForRun(int runIndex, Exception error = null)
        => stoppedCallbacks[runIndex]?.Invoke(error);

    public bool HasRenderCallback => renderFrames != null;
    public bool HasStoppedCallback => onStopped != null;
}
