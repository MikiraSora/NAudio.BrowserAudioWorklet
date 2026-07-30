using System;
using System.Threading.Tasks;

namespace NAudio.Wave.Browser;

/// <summary>
/// Fills <paramref name="destination"/> with up to <paramref name="frameCount"/> frames of
/// interleaved 32-bit float samples (little-endian bytes) and returns the number of frames
/// actually written. A return value of <c>0</c> signals end of stream.
/// </summary>
/// <remarks>
/// Invoked on the WebAssembly main thread, driven by the AudioWorklet's demand for data.
/// It must not block: it reads whatever the source has ready and returns promptly.
/// </remarks>
/// <param name="destination">Buffer to fill with interleaved float bytes.</param>
/// <param name="frameCount">Maximum number of frames requested.</param>
/// <returns>Frames actually written; <c>0</c> at end of stream.</returns>
internal delegate int AudioRenderCallback(Span<byte> destination, int frameCount);

/// <summary>
/// Transport seam between <see cref="BrowserAudioWorkletPlayer"/> and the browser's Web Audio
/// graph. The player owns format conversion and the state machine; the bridge moves rendered
/// frames across the managed/JavaScript boundary, controls the Web Audio gain node, and reports
/// when the graph stops. Isolating the JavaScript interop here keeps the player unit-testable
/// against a fake bridge with no browser or JS runtime.
/// </summary>
internal interface IAudioWorkletBridge : IDisposable
{
    /// <summary>
    /// Builds the Web Audio graph (an <c>AudioContext</c> feeding an <c>AudioWorkletNode</c>) and
    /// begins pulling audio through <paramref name="renderFrames"/>. Completes once the graph is
    /// created; the returned task faults if the worklet module, context, or node could not be
    /// created. A later asynchronous context failure is reported through
    /// <paramref name="onStopped"/>.
    /// </summary>
    /// <param name="sampleRate">Output sample rate in Hz.</param>
    /// <param name="channels">Output channel count.</param>
    /// <param name="bufferFrameCount">Target ring-buffer capacity, measured in audio frames.</param>
    /// <param name="renderFrames">Callback the bridge invokes to obtain interleaved float frames.</param>
    /// <param name="onStopped">
    /// Invoked exactly once when the graph stops - at end of stream, on a render/transport error
    /// (carrying the exception), or after <see cref="StopAsync"/>. Never invoked for an explicit
    /// stop, which the player reports itself.
    /// </param>
    Task StartAsync(
        int sampleRate,
        int channels,
        int bufferFrameCount,
        AudioRenderCallback renderFrames,
        Action<Exception> onStopped);

    /// <summary>Suspends the audio context, halting pulls without tearing down the graph.</summary>
    Task PauseAsync();

    /// <summary>Resumes a suspended audio context.</summary>
    Task ResumeAsync();

    /// <summary>Sets the output gain, where <c>1.0</c> is unity.</summary>
    void SetVolume(float volume);

    /// <summary>Stops pulling and tears down the audio graph.</summary>
    Task StopAsync();
}
