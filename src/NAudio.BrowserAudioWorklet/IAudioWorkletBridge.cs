using System;
using System.Threading.Tasks;

namespace NAudio.Wave.Browser;

/// <summary>
/// Fills <paramref name="destination"/> with up to <paramref name="frameCount"/> frames of
/// interleaved 32-bit float samples and returns the number of frames actually written. A return
/// value of <c>0</c> signals end of stream.
/// </summary>
/// <remarks>
/// Invoked on the WebAssembly main thread, driven by the AudioWorklet's demand for data.
/// It must not block: it reads whatever the source has ready and returns promptly.
/// </remarks>
/// <param name="destination">Reusable float buffer to fill with interleaved samples.</param>
/// <param name="frameCount">Maximum number of frames requested.</param>
/// <returns>Frames actually written; <c>0</c> at end of stream.</returns>
internal delegate int AudioRenderCallback(float[] destination, int frameCount);

internal readonly record struct AudioWorkletPreparation(
    int SampleRate,
    double BaseLatencySeconds,
    double OutputLatencySeconds);

internal readonly record struct AudioWorkletEvent(
    string Type,
    double ContextTimeSeconds,
    long MissingFrames,
    double EstimatedStartToOutputLatencySeconds = 0);

/// <summary>
/// Transport seam between <see cref="BrowserAudioWorkletPlayer"/> and the browser's Web Audio
/// graph. The player owns format conversion and the state machine; the bridge moves rendered
/// frames across the managed/JavaScript boundary, controls the Web Audio gain node, and reports
/// when the graph stops. Isolating the JavaScript interop here keeps the player unit-testable
/// against a fake bridge with no browser or JS runtime.
/// </summary>
internal interface IAudioWorkletBridge : IDisposable
{
    Task<AudioWorkletPreparation> PrepareAsync(
        int requestedSampleRate,
        int channels,
        bool useDeviceSampleRate);

    /// <summary>
    /// Starts a run on the prepared Web Audio graph and begins pulling audio through
    /// <paramref name="renderFrames"/>. Completes once the context has resumed; the returned task
    /// faults if the run could not start. A later asynchronous context failure is reported through
    /// <paramref name="onStopped"/>.
    /// </summary>
    /// <param name="channels">Output channel count.</param>
    /// <param name="bufferFrameCount">Target queued capacity, measured in audio frames.</param>
    /// <param name="initialBufferFrameCount">Frames requested for the latency-sensitive first block.</param>
    /// <param name="requestLeadTimeSeconds">
    /// Time already spent preparing this run after the caller requested playback.
    /// </param>
    /// <param name="renderFrames">Callback the bridge invokes to obtain interleaved float frames.</param>
    /// <param name="onStopped">
    /// Invoked exactly once when the graph stops - at end of stream, on a render/transport error
    /// (carrying the exception), or after <see cref="StopAsync"/>. Never invoked for an explicit
    /// stop, which the player reports itself.
    /// </param>
    /// <param name="onEvent">Receives first-frame and buffer-underrun diagnostics.</param>
    Task StartAsync(
        int channels,
        int bufferFrameCount,
        int initialBufferFrameCount,
        double requestLeadTimeSeconds,
        AudioRenderCallback renderFrames,
        Action<Exception> onStopped,
        Action<AudioWorkletEvent> onEvent);

    /// <summary>Flushes queued samples and starts a fresh feed run on the existing graph.</summary>
    Task FlushAsync();

    /// <summary>Suspends the audio context, halting pulls without tearing down the graph.</summary>
    Task PauseAsync();

    /// <summary>Resumes a suspended audio context.</summary>
    Task ResumeAsync();

    /// <summary>Sets the output gain, where <c>1.0</c> is unity.</summary>
    void SetVolume(float volume);

    /// <summary>Stops pulling, clears queued samples, and suspends the persistent graph.</summary>
    Task StopAsync();

    Task<BrowserAudioPlaybackMetrics> GetMetricsAsync();
}
