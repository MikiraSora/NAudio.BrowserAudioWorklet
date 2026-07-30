using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NAudio.Wave.Browser;

/// <summary>
/// An <see cref="IWavePlayer"/> that plays audio in the browser through the Web Audio
/// <c>AudioWorklet</c> API, letting Avalonia's Browser platform (or any .NET WebAssembly app)
/// play any NAudio <see cref="IWaveProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Input is converted to interleaved 32-bit floating-point samples before it crosses the
/// JavaScript boundary. Volume is applied by the Web Audio gain node, so buffered audio responds
/// immediately and gain is never applied twice.
/// </para>
/// <para>
/// Because browser WebAssembly is single-threaded and the audio graph pulls on demand, there is
/// no background feeder thread (unlike the desktop backends). The <c>AudioWorkletProcessor</c>
/// running on the browser's audio thread drains a ring buffer; whenever it runs low it
/// asks the main thread to top up, which reads this player's sample provider and
/// pushes more frames across the boundary. All interaction with JavaScript is hidden behind an
/// internal bridge, so the player itself carries only the state machine and format conversion.
/// </para>
/// <para>
/// Browser autoplay policies require the audio context to be created from a user gesture, so
/// <see cref="Play"/> starts the graph asynchronously. Use <see cref="PlaybackStopped"/> to learn
/// when playback actually ends (end of stream or error).
/// </para>
/// </remarks>
public sealed class BrowserAudioWorkletPlayer : IWavePlayer
{
    private const int DefaultBufferDurationMilliseconds = 250;
    private const int MinimumBufferDurationMilliseconds = 20;
    private const int MaximumBufferDurationMilliseconds = 5000;

    private readonly object sync = new();
    private readonly IAudioWorkletBridge bridge;
    private readonly int bufferDurationMilliseconds;
    private float[] renderSamples = Array.Empty<float>();
    private ISampleProvider source;
    private float volume = 1.0f;
    private volatile PlaybackState playbackState = PlaybackState.Stopped;
    private Task transportTask = Task.CompletedTask;
    private int nextRunId;
    private int activeRunId;
    private bool stoppedRaised;
    private bool isDisposed;

    /// <summary>Creates a <see cref="BrowserAudioWorkletPlayer"/> backed by the Web Audio graph.</summary>
    public BrowserAudioWorkletPlayer()
        : this(DefaultBufferDurationMilliseconds)
    {
    }

    /// <summary>
    /// Creates a player with the requested AudioWorklet buffer duration.
    /// </summary>
    /// <param name="bufferDurationMilliseconds">
    /// Target number of milliseconds kept in the worklet ring buffer. Larger values tolerate
    /// main-thread stalls better; smaller values reduce transport latency.
    /// </param>
    public BrowserAudioWorkletPlayer(int bufferDurationMilliseconds)
        : this(CreateDefaultBridge(), bufferDurationMilliseconds)
    {
    }

    /// <summary>
    /// Test/advanced constructor: injects the transport bridge. Keeps the JavaScript interop out
    /// of unit tests, which supply a fake bridge.
    /// </summary>
    internal BrowserAudioWorkletPlayer(IAudioWorkletBridge bridge)
        : this(bridge, DefaultBufferDurationMilliseconds)
    {
    }

    internal BrowserAudioWorkletPlayer(IAudioWorkletBridge bridge, int bufferDurationMilliseconds)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        if (bufferDurationMilliseconds < MinimumBufferDurationMilliseconds ||
            bufferDurationMilliseconds > MaximumBufferDurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bufferDurationMilliseconds),
                $"Buffer duration must be between {MinimumBufferDurationMilliseconds} and " +
                $"{MaximumBufferDurationMilliseconds} milliseconds.");
        }

        this.bufferDurationMilliseconds = bufferDurationMilliseconds;
    }

    private static IAudioWorkletBridge CreateDefaultBridge()
    {
#if BROWSER
        return new AudioWorkletBridge();
#else
        throw new PlatformNotSupportedException(
            "BrowserAudioWorkletPlayer's default constructor only works in a browser (WebAssembly) app. " +
            "On other platforms, supply an IWavePlayer suited to that platform.");
#endif
    }

    /// <inheritdoc />
    public PlaybackState PlaybackState => playbackState;

    /// <inheritdoc />
    public WaveFormat OutputWaveFormat { get; private set; }

    /// <inheritdoc />
    public event EventHandler<StoppedEventArgs> PlaybackStopped;

    /// <inheritdoc />
    public float Volume
    {
        get => volume;
        set
        {
            if (!float.IsFinite(value) || value < 0.0f || value > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Volume must be between 0.0 and 1.0.");
            }

            lock (sync)
            {
                ThrowIfDisposed();
                volume = value;
            }

            // The bridge remembers this value until the graph exists, then applies it through a
            // GainNode. Samples are deliberately left at unity in managed code so gain is not
            // applied twice and already-buffered audio reacts immediately to volume changes.
            bridge.SetVolume(value);
        }
    }

    /// <inheritdoc />
    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        lock (sync)
        {
            ThrowIfDisposed();
            if (source != null)
            {
                throw new InvalidOperationException("Already initialised");
            }

            source = waveProvider.ToSampleProvider();
            if (source.WaveFormat.Channels > 32)
            {
                source = null;
                throw new NotSupportedException("Web Audio supports at most 32 output channels.");
            }

            OutputWaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate, source.WaveFormat.Channels);
        }
    }

    /// <summary>
    /// Render callback handed to the bridge. Reads float samples from the source sample provider
    /// into a reusable array, copies their bit-identical representation into the destination byte
    /// span, and returns the number of frames produced. Runs on the WebAssembly main thread.
    /// </summary>
    private int RenderFrames(Span<byte> destination, int frameCount)
    {
        var localSource = source;
        if (localSource == null)
        {
            return 0;
        }

        int channels = localSource.WaveFormat.Channels;
        int samplesRequested = Math.Min(frameCount * channels, destination.Length / sizeof(float));
        if (renderSamples.Length < samplesRequested)
        {
            renderSamples = new float[samplesRequested];
        }

        int samplesRead = localSource.Read(renderSamples, 0, samplesRequested);
        if (samplesRead % channels != 0)
        {
            throw new InvalidOperationException("The source returned a partial audio frame.");
        }

        var destinationSamples = MemoryMarshal.Cast<byte, float>(destination).Slice(0, samplesRead);
        renderSamples.AsSpan(0, samplesRead).CopyTo(destinationSamples);

        return samplesRead / channels;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Starting the audio graph is asynchronous (the browser only permits it from a user gesture),
    /// so this returns as soon as the state flips to <see cref="PlaybackState.Playing"/>; the graph
    /// spins up in the background. A failure to build the graph surfaces through
    /// <see cref="PlaybackStopped"/> rather than being thrown here.
    /// </remarks>
    public void Play()
        => _ = ObserveTransportAsync(PlayAsync());

    /// <summary>
    /// Starts or resumes playback and completes when the browser graph has accepted the request.
    /// Call this method directly from a click or tap handler so browser autoplay policy can
    /// associate <c>AudioContext.resume()</c> with the user gesture.
    /// </summary>
    /// <returns>A task that faults if the Web Audio graph cannot be started or resumed.</returns>
    public Task PlayAsync()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (source == null)
            {
                throw new InvalidOperationException("Call Init before Play");
            }

            switch (playbackState)
            {
                case PlaybackState.Playing:
                    return transportTask;
                case PlaybackState.Paused:
                    playbackState = PlaybackState.Playing;
                    transportTask = ResumeGraphAsync(activeRunId);
                    return transportTask;
                default:
                    playbackState = PlaybackState.Playing;
                    stoppedRaised = false;
                    activeRunId = ++nextRunId;
                    transportTask = StartGraphAsync(activeRunId);
                    return transportTask;
            }
        }
    }

    private async Task StartGraphAsync(int runId)
    {
        try
        {
            int bufferFrameCount = Math.Max(
                512,
                (int)Math.Ceiling(OutputWaveFormat.SampleRate * bufferDurationMilliseconds / 1000.0));
            await bridge.StartAsync(
                OutputWaveFormat.SampleRate,
                OutputWaveFormat.Channels,
                bufferFrameCount,
                RenderFrames,
                error => OnBridgeStopped(runId, error));
            bridge.SetVolume(volume);
        }
        catch (Exception ex)
        {
            OnBridgeStopped(runId, NormalizeBrowserException("Unable to start browser audio playback.", ex));
            throw;
        }
    }

    private async Task ResumeGraphAsync(int runId)
    {
        try
        {
            await bridge.ResumeAsync();
        }
        catch (Exception ex)
        {
            OnBridgeStopped(runId, NormalizeBrowserException("Unable to resume browser audio playback.", ex));
            throw;
        }
    }

    private static async Task ObserveTransportAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // PlayAsync exposes the exception to callers that can await it. The IWavePlayer.Play
            // compatibility path reports the same failure through PlaybackStopped.
        }
    }

    /// <inheritdoc />
    public void Pause()
    {
        int runId;
        lock (sync)
        {
            if (playbackState != PlaybackState.Playing)
            {
                return;
            }

            playbackState = PlaybackState.Paused;
            runId = activeRunId;
        }

        _ = PauseGraphAsync(runId);
    }

    private async Task PauseGraphAsync(int runId)
    {
        try
        {
            await bridge.PauseAsync();
        }
        catch (Exception ex)
        {
            OnBridgeStopped(runId, NormalizeBrowserException("Unable to pause browser audio playback.", ex));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// An explicit stop tears down the graph and raises <see cref="PlaybackStopped"/> with a
    /// <c>null</c> exception, matching the other NAudio backends.
    /// </remarks>
    public void Stop()
    {
        lock (sync)
        {
            if (playbackState == PlaybackState.Stopped)
            {
                return;
            }

            playbackState = PlaybackState.Stopped;
            activeRunId = 0;
            transportTask = Task.CompletedTask;
        }

        _ = StopGraphAsync();
        RaisePlaybackStopped(null);
    }

    private async Task StopGraphAsync()
    {
        try
        {
            await bridge.StopAsync();
        }
        catch (Exception)
        {
            // Teardown failures are not actionable by the caller and must not mask an
            // already-reported stop; swallow them.
        }
    }

    /// <summary>
    /// Invoked by the bridge when the graph stops on its own - natural end of stream or a
    /// render/transport error. An explicit <see cref="Stop"/> reports itself, so this only fires
    /// for graph-initiated stops.
    /// </summary>
    private void OnBridgeStopped(int runId, Exception error)
    {
        lock (sync)
        {
            if (isDisposed || runId == 0 || runId != activeRunId)
            {
                return;
            }

            playbackState = PlaybackState.Stopped;
            activeRunId = 0;
            transportTask = Task.CompletedTask;
        }

        RaisePlaybackStopped(error);
    }

    private static Exception NormalizeBrowserException(string message, Exception error)
        => error is BrowserAudioException ? error : new BrowserAudioException(message, error);

    private void RaisePlaybackStopped(Exception error)
    {
        lock (sync)
        {
            if (stoppedRaised)
            {
                return;
            }

            stoppedRaised = true;
        }

        PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (sync)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            playbackState = PlaybackState.Stopped;
            activeRunId = 0;
            transportTask = Task.CompletedTask;
        }

        bridge.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (isDisposed)
        {
            throw new ObjectDisposedException(nameof(BrowserAudioWorkletPlayer));
        }
    }
}
