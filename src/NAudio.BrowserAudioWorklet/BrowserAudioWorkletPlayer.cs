using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NAudio.Wave.SampleProviders;

namespace NAudio.Wave.Browser;

/// <summary>
/// An <see cref="IWavePlayer"/> that plays audio in the browser through the Web Audio
/// <c>AudioWorklet</c> API, letting Avalonia's Browser platform (or any .NET WebAssembly app)
/// play any NAudio <see cref="IWaveProvider"/> or <see cref="ISampleProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Input crosses the JavaScript boundary as interleaved 32-bit floating-point samples. Volume is
/// applied by the Web Audio gain node, so buffered audio responds immediately and gain is never
/// applied twice.
/// </para>
/// <para>
/// The processor runs on the browser audio thread and consumes transferable sample blocks. It
/// asks the WebAssembly main thread to top up at a low-water mark. The first request is deliberately
/// small so playback can begin before the rest of the target buffer is filled.
/// </para>
/// <para>
/// <see cref="PrepareAsync"/> creates the suspended audio graph and loads the processor ahead of
/// playback. <see cref="PlayAsync"/> then resumes that persistent graph from a user gesture.
/// <see cref="Stop"/> preserves the prepared graph; only <see cref="Dispose"/> closes its
/// <c>AudioContext</c>.
/// </para>
/// </remarks>
public sealed class BrowserAudioWorkletPlayer : IWavePlayer
{
    private const int MinimumBufferDurationMilliseconds = 20;
    private const int MaximumBufferDurationMilliseconds = 5000;
    private const int MinimumInitialBufferFrameCount = 128;
    private const int MaximumInitialBufferFrameCount = 8192;
    private const int MinimumTransportBufferFrameCount = 512;

    private readonly object sync = new();
    private readonly IAudioWorkletBridge bridge;
    private readonly BrowserAudioWorkletOptions options;
    private ISampleProvider source;
    private ISampleProvider renderSource;
    private ISeekableSampleProvider seekableSource;
    private WaveStream waveStreamSource;
    private float volume = 1.0f;
    private volatile PlaybackState playbackState = PlaybackState.Stopped;
    private Task transportTask = Task.CompletedTask;
    private Task<BrowserAudioLatencyInfo> preparationTask;
    private int nextRunId;
    private int activeRunId;
    private bool stoppedRaised;
    private bool isDisposed;

    /// <summary>Creates a playback-oriented player backed by the Web Audio graph.</summary>
    public BrowserAudioWorkletPlayer()
        : this(BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Playback))
    {
    }

    /// <summary>Creates a player with the requested target buffer duration.</summary>
    public BrowserAudioWorkletPlayer(int bufferDurationMilliseconds)
        : this(new BrowserAudioWorkletOptions { BufferDurationMilliseconds = bufferDurationMilliseconds })
    {
    }

    /// <summary>Creates a player using a latency-oriented preset.</summary>
    public BrowserAudioWorkletPlayer(BrowserAudioLatencyProfile profile)
        : this(BrowserAudioWorkletOptions.ForProfile(profile))
    {
    }

    /// <summary>Creates a player using explicit transport options.</summary>
    public BrowserAudioWorkletPlayer(BrowserAudioWorkletOptions options)
        : this(CreateDefaultBridge(), options)
    {
    }

    /// <summary>Test constructor that keeps JavaScript interop behind an injected bridge.</summary>
    internal BrowserAudioWorkletPlayer(IAudioWorkletBridge bridge)
        : this(bridge, BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Playback))
    {
    }

    internal BrowserAudioWorkletPlayer(IAudioWorkletBridge bridge, int bufferDurationMilliseconds)
        : this(bridge, new BrowserAudioWorkletOptions
        {
            BufferDurationMilliseconds = bufferDurationMilliseconds,
        })
    {
    }

    internal BrowserAudioWorkletPlayer(IAudioWorkletBridge bridge, BrowserAudioWorkletOptions options)
    {
        this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        this.options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.BufferDurationMilliseconds < MinimumBufferDurationMilliseconds ||
            options.BufferDurationMilliseconds > MaximumBufferDurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Buffer duration must be between {MinimumBufferDurationMilliseconds} and " +
                $"{MaximumBufferDurationMilliseconds} milliseconds.");
        }

        if (options.InitialBufferFrameCount < MinimumInitialBufferFrameCount ||
            options.InitialBufferFrameCount > MaximumInitialBufferFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Initial buffer must be between {MinimumInitialBufferFrameCount} and " +
                $"{MaximumInitialBufferFrameCount} frames.");
        }
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

    /// <summary>Latest browser-reported latency information, available after preparation.</summary>
    public BrowserAudioLatencyInfo LatencyInfo { get; private set; }

    /// <inheritdoc />
    public event EventHandler<StoppedEventArgs> PlaybackStopped;

    /// <summary>Raised once per run when the processor renders its first source frame.</summary>
    public event EventHandler<BrowserAudioFirstFrameEventArgs> FirstFrameRendered;

    /// <summary>Raised after the processor recovers from a period of missing source frames.</summary>
    public event EventHandler<BrowserAudioUnderrunEventArgs> BufferUnderrun;

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

            // Gain remains in Web Audio so queued samples react immediately to volume changes.
            bridge.SetVolume(value);
        }
    }

    /// <inheritdoc />
    public void Init(IWaveProvider waveProvider)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);
        ISampleProvider sampleProvider = waveProvider.ToSampleProvider();
        InitCore(sampleProvider, waveProvider as WaveStream, null);
    }

    /// <summary>
    /// Initializes directly from an <see cref="ISampleProvider"/>, avoiding NAudio's
    /// sample-to-wave-to-sample adapter round trip.
    /// </summary>
    public void Init(ISampleProvider sampleProvider)
    {
        ArgumentNullException.ThrowIfNull(sampleProvider);
        InitCore(sampleProvider, null, sampleProvider as ISeekableSampleProvider);
    }

    private void InitCore(
        ISampleProvider sampleProvider,
        WaveStream waveStream,
        ISeekableSampleProvider seekable)
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (source != null)
            {
                throw new InvalidOperationException("Already initialised");
            }

            if (sampleProvider.WaveFormat.Channels > 32)
            {
                throw new NotSupportedException("Web Audio supports at most 32 output channels.");
            }

            source = sampleProvider;
            renderSource = sampleProvider;
            waveStreamSource = waveStream;
            seekableSource = seekable;
            OutputWaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                sampleProvider.WaveFormat.SampleRate,
                sampleProvider.WaveFormat.Channels);
        }
    }

    /// <summary>
    /// Loads the JavaScript modules and creates a suspended AudioContext and AudioWorkletNode.
    /// Call this after <see cref="Init(IWaveProvider)"/> and before the latency-sensitive gesture.
    /// </summary>
    public Task<BrowserAudioLatencyInfo> PrepareAsync()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (source == null)
            {
                throw new InvalidOperationException("Call Init before PrepareAsync");
            }

            if (preparationTask?.IsFaulted == true || preparationTask?.IsCanceled == true)
            {
                preparationTask = null;
            }

            return preparationTask ??= PrepareCoreAsync();
        }
    }

    private async Task<BrowserAudioLatencyInfo> PrepareCoreAsync()
    {
        try
        {
            AudioWorkletPreparation preparation = await bridge.PrepareAsync(
                source.WaveFormat.SampleRate,
                source.WaveFormat.Channels,
                options.UseDeviceSampleRate);

            BrowserAudioLatencyInfo latency;
            lock (sync)
            {
                ThrowIfDisposed();
                ConfigureRenderSource(preparation.SampleRate);
                int bufferFrameCount = CalculateBufferFrameCount(preparation.SampleRate);
                latency = new BrowserAudioLatencyInfo(
                    preparation.SampleRate,
                    preparation.BaseLatencySeconds,
                    preparation.OutputLatencySeconds,
                    bufferFrameCount);
                LatencyInfo = latency;
            }

            bridge.SetVolume(volume);
            return latency;
        }
        catch
        {
            lock (sync)
            {
                preparationTask = null;
            }

            throw;
        }
    }

    private int CalculateBufferFrameCount(int sampleRate)
        => Math.Max(
            MinimumTransportBufferFrameCount,
            checked((int)Math.Ceiling(sampleRate * options.BufferDurationMilliseconds / 1000.0)));

    private void ConfigureRenderSource(int outputSampleRate)
    {
        renderSource = source.WaveFormat.SampleRate == outputSampleRate
            ? source
            : new WdlResamplingSampleProvider(source, outputSampleRate);
        OutputWaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            outputSampleRate,
            source.WaveFormat.Channels);
    }

    /// <summary>
    /// Render callback handed to the bridge. The bridge owns and reuses the destination array, so
    /// the sample provider writes directly into the memory exposed to JavaScript.
    /// </summary>
    private int RenderFrames(float[] destination, int frameCount)
    {
        ISampleProvider localSource = renderSource;
        if (localSource == null)
        {
            return 0;
        }

        int channels = localSource.WaveFormat.Channels;
        int samplesRequested = Math.Min(checked(frameCount * channels), destination.Length);
        int samplesRead = localSource.Read(destination, 0, samplesRequested);
        if (samplesRead % channels != 0)
        {
            throw new InvalidOperationException("The source returned a partial audio frame.");
        }

        return samplesRead / channels;
    }

    /// <inheritdoc />
    public void Play()
        => _ = ObserveTransportAsync(PlayAsync());

    /// <summary>
    /// Starts or resumes playback and completes when the persistent browser graph accepts the
    /// request. Call directly from a click or tap handler so <c>AudioContext.resume()</c> retains
    /// user activation.
    /// </summary>
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
                    long requestTimestamp = Stopwatch.GetTimestamp();
                    if (LatencyInfo != null)
                    {
                        ConfigureRenderSource(LatencyInfo.SampleRate);
                    }

                    transportTask = StartGraphAsync(activeRunId, requestTimestamp);
                    return transportTask;
            }
        }
    }

    private async Task StartGraphAsync(int runId, long requestTimestamp)
    {
        try
        {
            BrowserAudioLatencyInfo latency = await PrepareAsync();
            if (!IsActiveRun(runId))
            {
                return;
            }

            int initialFrameCount = Math.Min(
                latency.BufferFrameCount,
                options.InitialBufferFrameCount);
            double requestLeadTimeSeconds = Stopwatch.GetElapsedTime(requestTimestamp).TotalSeconds;
            await bridge.StartAsync(
                OutputWaveFormat.Channels,
                latency.BufferFrameCount,
                initialFrameCount,
                requestLeadTimeSeconds,
                RenderFrames,
                error => OnBridgeStopped(runId, error),
                workletEvent => OnBridgeEvent(runId, workletEvent));
            bridge.SetVolume(volume);
        }
        catch (Exception ex)
        {
            OnBridgeStopped(runId, NormalizeBrowserException("Unable to start browser audio playback.", ex));
            throw;
        }
    }

    private bool IsActiveRun(int runId)
    {
        lock (sync)
        {
            return !isDisposed &&
                   playbackState != PlaybackState.Stopped &&
                   activeRunId == runId;
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
            // PlayAsync exposes the exception; IWavePlayer.Play reports it through PlaybackStopped.
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

    /// <summary>
    /// Discards audio already queued in the worklet and restarts feeding from the source's current
    /// position without recreating the AudioContext or AudioWorkletNode.
    /// </summary>
    public async Task FlushAsync()
    {
        int runId;
        bool hasActiveRun;
        lock (sync)
        {
            ThrowIfDisposed();
            if (source == null)
            {
                throw new InvalidOperationException("Call Init before FlushAsync");
            }

            if (LatencyInfo != null)
            {
                ConfigureRenderSource(LatencyInfo.SampleRate);
            }

            runId = activeRunId;
            hasActiveRun = playbackState != PlaybackState.Stopped && runId != 0;
        }

        if (!hasActiveRun)
        {
            return;
        }

        try
        {
            await bridge.FlushAsync();
        }
        catch (Exception ex)
        {
            OnBridgeStopped(runId, NormalizeBrowserException("Unable to flush browser audio playback.", ex));
            throw;
        }
    }

    /// <summary>Seeks a compatible source and flushes queued samples without rebuilding the graph.</summary>
    public Task SeekAsync(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        lock (sync)
        {
            ThrowIfDisposed();
            if (seekableSource != null)
            {
                seekableSource.Position = position > seekableSource.Duration
                    ? seekableSource.Duration
                    : position;
            }
            else if (waveStreamSource != null)
            {
                waveStreamSource.CurrentTime = position > waveStreamSource.TotalTime
                    ? waveStreamSource.TotalTime
                    : position;
            }
            else
            {
                throw new NotSupportedException(
                    "The initialized source does not implement ISeekableSampleProvider and is not a WaveStream.");
            }
        }

        return FlushAsync();
    }

    /// <summary>Returns counters collected by the current AudioWorklet run.</summary>
    public Task<BrowserAudioPlaybackMetrics> GetPlaybackMetricsAsync()
    {
        lock (sync)
        {
            ThrowIfDisposed();
            if (LatencyInfo == null)
            {
                return Task.FromResult(new BrowserAudioPlaybackMetrics(0, 0, null, false));
            }
        }

        return bridge.GetMetricsAsync();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Explicit stop clears the current processor run and suspends the persistent context. It
    /// raises <see cref="PlaybackStopped"/> with a null exception, matching other NAudio backends.
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
        catch
        {
            // Teardown failures must not mask an already-reported explicit stop.
        }
    }

    private void OnBridgeEvent(int runId, AudioWorkletEvent workletEvent)
    {
        BrowserAudioLatencyInfo latency;
        lock (sync)
        {
            if (isDisposed || runId == 0 || runId != activeRunId)
            {
                return;
            }

            latency = LatencyInfo;
        }

        if (workletEvent.Type == "first-frame" && latency != null)
        {
            FirstFrameRendered?.Invoke(
                this,
                new BrowserAudioFirstFrameEventArgs(
                    workletEvent.ContextTimeSeconds,
                    workletEvent.EstimatedStartToOutputLatencySeconds,
                    latency));
        }
        else if (workletEvent.Type == "underrun")
        {
            BufferUnderrun?.Invoke(this, new BrowserAudioUnderrunEventArgs(workletEvent.MissingFrames));
        }
    }

    /// <summary>
    /// Invoked when the graph stops on its own: natural end of stream or a render/transport error.
    /// An explicit <see cref="Stop"/> reports itself and invalidates the run first.
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
