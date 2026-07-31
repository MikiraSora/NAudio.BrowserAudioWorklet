#if BROWSER
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace NAudio.Wave.Browser;

/// <summary>
/// Owns a persistent Web Audio graph and feeds it through source blocks requested by the
/// AudioWorklet. Run generations isolate late demand and diagnostics after stop or flush.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class AudioWorkletBridge : IAudioWorkletBridge
{
    private const string ModuleName = "naudio-audio-worklet";
    private const string ModuleUrl = "../_content/NAudio.BrowserAudioWorklet/naudio-audio-worklet.js";

    private static readonly object ModuleLock = new();
    private static Task moduleLoad;
    private static int nextHandle;

    private readonly object sync = new();
    private readonly int handle = Interlocked.Increment(ref nextHandle);
    private Task<AudioWorkletPreparation> preparationTask;
    private float[] renderBuffer = Array.Empty<float>();
    private AudioRenderCallback renderFrames;
    private Action<Exception> onStopped;
    private Action<AudioWorkletEvent> onEvent;
    private int channels;
    private int bufferFrameCount;
    private int initialBufferFrameCount;
    private int generation;
    private int currentRunId;
    private bool prepared;
    private bool graphStarted;
    private bool paused;
    private bool disposed;
    private float volume = 1.0f;

    private static Task EnsureModuleAsync()
    {
        Task load;
        lock (ModuleLock)
        {
            load = moduleLoad ??= JSHost.ImportAsync(ModuleName, ModuleUrl);
        }

        return ObserveModuleLoadAsync(load);
    }

    private static async Task ObserveModuleLoadAsync(Task load)
    {
        try
        {
            await load;
        }
        catch
        {
            // A transient network failure must not poison every future player instance.
            lock (ModuleLock)
            {
                if (ReferenceEquals(moduleLoad, load))
                {
                    moduleLoad = null;
                }
            }

            throw;
        }
    }

    public Task<AudioWorkletPreparation> PrepareAsync(
        int requestedSampleRate,
        int requestedChannels,
        bool useDeviceSampleRate)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (preparationTask?.IsFaulted == true || preparationTask?.IsCanceled == true)
            {
                preparationTask = null;
            }

            return preparationTask ??= PrepareCoreAsync(
                requestedSampleRate,
                requestedChannels,
                useDeviceSampleRate);
        }
    }

    private async Task<AudioWorkletPreparation> PrepareCoreAsync(
        int requestedSampleRate,
        int requestedChannels,
        bool useDeviceSampleRate)
    {
        try
        {
            await EnsureModuleAsync();
            using JSObject result = await Interop.PrepareAsync(
                handle,
                requestedSampleRate,
                requestedChannels,
                useDeviceSampleRate);
            var preparation = new AudioWorkletPreparation(
                result.GetPropertyAsInt32("sampleRate"),
                result.GetPropertyAsDouble("baseLatency"),
                result.GetPropertyAsDouble("outputLatency"));

            float initialVolume;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                prepared = true;
                initialVolume = volume;
            }

            Interop.SetVolume(handle, initialVolume);
            return preparation;
        }
        catch
        {
            lock (sync)
            {
                prepared = false;
            }

            throw;
        }
    }

    public async Task StartAsync(
        int requestedChannels,
        int requestedBufferFrameCount,
        int requestedInitialBufferFrameCount,
        double requestLeadTimeSeconds,
        AudioRenderCallback requestedRenderFrames,
        Action<Exception> requestedOnStopped,
        Action<AudioWorkletEvent> requestedOnEvent)
    {
        ArgumentNullException.ThrowIfNull(requestedRenderFrames);

        int runId;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!prepared)
            {
                throw new InvalidOperationException("PrepareAsync must complete before StartAsync.");
            }

            runId = ++generation;
            currentRunId = runId;
            graphStarted = false;
            paused = false;
            channels = requestedChannels;
            bufferFrameCount = requestedBufferFrameCount;
            initialBufferFrameCount = requestedInitialBufferFrameCount;
            renderFrames = requestedRenderFrames;
            onStopped = requestedOnStopped;
            onEvent = requestedOnEvent;
        }

        try
        {
            Interop.BeginStart(
                handle,
                runId,
                requestedBufferFrameCount,
                requestedInitialBufferFrameCount,
                requestLeadTimeSeconds);

            // Prime the first block before resume. Messages posted to the processor are ordered,
            // so its start and sample blocks are ready before the first render quantum.
            int initialFramesRendered = RenderAndEnqueue(runId, requestedInitialBufferFrameCount);
            Task resumeTask = Interop.ResumeAsync(handle);
            if (initialFramesRendered > 0)
            {
                _ = FeedLoopAsync(runId, requestedOnStopped);
            }
            else
            {
                _ = DrainInitialRunAsync(runId, requestedOnStopped);
            }

            _ = EventLoopAsync(runId, requestedOnStopped, requestedOnEvent);
            await resumeTask;
            if (!IsCurrent(runId))
            {
                await StopRunSilentlyAsync(runId);
                return;
            }

            bool pauseAfterStart;
            float initialVolume;
            lock (sync)
            {
                graphStarted = true;
                pauseAfterStart = paused;
                initialVolume = volume;
            }

            Interop.SetVolume(handle, initialVolume);
            if (pauseAfterStart)
            {
                await Interop.PauseAsync(handle);
            }

        }
        catch (Exception ex)
        {
            await StopRunSilentlyAsync(runId);
            if (IsCurrent(runId))
            {
                Invalidate(runId);
            }

            throw ToBrowserException("The browser audio graph could not be started.", ex);
        }
    }

    private int RenderAndEnqueue(int runId, int framesNeeded)
    {
        int requiredSamples = checked(framesNeeded * channels);
        if (renderBuffer.Length < requiredSamples)
        {
            renderBuffer = new float[requiredSamples];
        }

        int framesRendered = renderFrames(renderBuffer, framesNeeded);
        if (framesRendered > framesNeeded)
        {
            throw new InvalidOperationException("The audio renderer returned more frames than requested.");
        }

        if (framesRendered > 0)
        {
            Interop.Enqueue(
                handle,
                runId,
                MemoryMarshal.AsBytes(
                    renderBuffer.AsSpan(0, checked(framesRendered * channels))),
                framesRendered);
        }

        return framesRendered;
    }

    private async Task FeedLoopAsync(int runId, Action<Exception> stoppedCallback)
    {
        Exception error = null;
        try
        {
            while (IsCurrent(runId))
            {
                int framesNeeded = await Interop.WaitForDemandAsync(handle, runId);
                if (framesNeeded <= 0 || !IsCurrent(runId))
                {
                    return;
                }

                int framesRendered = RenderAndEnqueue(runId, framesNeeded);
                if (framesRendered <= 0)
                {
                    await Interop.DrainAsync(handle, runId);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = ToBrowserException("Browser audio playback failed.", ex);
        }

        await FinishRunAsync(runId, stoppedCallback, error);
    }

    private async Task DrainInitialRunAsync(int runId, Action<Exception> stoppedCallback)
    {
        Exception error = null;
        try
        {
            await Interop.DrainAsync(handle, runId);
        }
        catch (Exception ex)
        {
            error = ToBrowserException("Browser audio playback failed.", ex);
        }

        await FinishRunAsync(runId, stoppedCallback, error);
    }

    private async Task EventLoopAsync(
        int runId,
        Action<Exception> stoppedCallback,
        Action<AudioWorkletEvent> eventCallback)
    {
        try
        {
            while (IsCurrent(runId))
            {
                using JSObject message = await Interop.WaitForEventAsync(handle, runId);
                string type = message.GetPropertyAsString("type");
                if (type == "stopped" || !IsCurrent(runId))
                {
                    return;
                }

                var workletEvent = type switch
                {
                    "first-frame" => new AudioWorkletEvent(
                        type,
                        message.GetPropertyAsDouble("contextTime"),
                        0,
                        message.GetPropertyAsDouble("startToOutputLatency")),
                    "underrun" => new AudioWorkletEvent(
                        type,
                        0,
                        checked((long)message.GetPropertyAsDouble("frames"))),
                    _ => default,
                };

                if (workletEvent.Type != null)
                {
                    eventCallback?.Invoke(workletEvent);
                }
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(runId))
            {
                await FinishRunAsync(
                    runId,
                    stoppedCallback,
                    ToBrowserException("Browser audio diagnostics failed.", ex));
            }
        }
    }

    public Task FlushAsync()
    {
        int runId;
        Action<Exception> stoppedCallback;
        Action<AudioWorkletEvent> eventCallback;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!graphStarted || currentRunId == 0)
            {
                return Task.CompletedTask;
            }

            runId = ++generation;
            currentRunId = runId;
            stoppedCallback = onStopped;
            eventCallback = onEvent;
        }

        try
        {
            Interop.Flush(handle, runId, bufferFrameCount, initialBufferFrameCount);
            _ = FeedLoopAsync(runId, stoppedCallback);
            _ = EventLoopAsync(runId, stoppedCallback, eventCallback);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Invalidate(runId);
            return Task.FromException(ToBrowserException("The browser audio buffer could not be flushed.", ex));
        }
    }

    public Task PauseAsync()
    {
        bool canCallInterop;
        lock (sync)
        {
            paused = true;
            canCallInterop = graphStarted && currentRunId != 0;
        }

        return canCallInterop ? Interop.PauseAsync(handle) : Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        bool canCallInterop;
        lock (sync)
        {
            paused = false;
            canCallInterop = graphStarted && currentRunId != 0;
        }

        return canCallInterop ? Interop.ResumeAsync(handle) : Task.CompletedTask;
    }

    public void SetVolume(float newVolume)
    {
        bool canCallInterop;
        lock (sync)
        {
            volume = newVolume;
            canCallInterop = prepared && !disposed;
        }

        if (canCallInterop)
        {
            Interop.SetVolume(handle, newVolume);
        }
    }

    public async Task StopAsync()
    {
        int runId;
        lock (sync)
        {
            runId = currentRunId;
            generation++;
            currentRunId = 0;
            graphStarted = false;
            paused = false;
        }

        if (runId != 0)
        {
            await Interop.StopAsync(handle, runId);
        }
    }

    public long TotalConsumedFrameCount
    {
        get
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!prepared)
                {
                    return 0;
                }

                int low = Interop.CaptureTotalConsumedFrameCountLow(handle);
                int high = Interop.GetCapturedTotalConsumedFrameCountHigh(handle);
                return ((long)high << 32) | (uint)low;
            }
        }
    }

    public Task ResetTotalConsumedAsync()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return prepared ? ResetTotalConsumedCoreAsync() : Task.CompletedTask;
        }
    }

    private async Task ResetTotalConsumedCoreAsync()
    {
        try
        {
            await Interop.ResetTotalConsumedAsync(handle);
        }
        catch (Exception ex)
        {
            throw ToBrowserException("The consumed audio counter could not be reset.", ex);
        }
    }

    public async Task<BrowserAudioPlaybackMetrics> GetMetricsAsync()
    {
        await EnsureModuleAsync();
        using JSObject metrics = Interop.GetMetrics(handle);
        double firstFrame = metrics.GetPropertyAsDouble("firstFrameContextTime");
        bool hasFirstFrame = metrics.GetPropertyAsBoolean("hasFirstFrame");
        bool hasStartToOutputLatency = metrics.GetPropertyAsBoolean("hasStartToOutputLatency");
        return new BrowserAudioPlaybackMetrics(
            metrics.GetPropertyAsInt32("underrunCount"),
            checked((long)metrics.GetPropertyAsDouble("underrunFrames")),
            hasFirstFrame ? firstFrame : null,
            hasFirstFrame,
            hasStartToOutputLatency
                ? metrics.GetPropertyAsDouble("startToOutputLatencySeconds")
                : null);
    }

    private async Task FinishRunAsync(
        int runId,
        Action<Exception> stoppedCallback,
        Exception error)
    {
        if (!Invalidate(runId))
        {
            return;
        }

        await StopRunSilentlyAsync(runId);
        stoppedCallback?.Invoke(error);
    }

    private bool IsCurrent(int runId)
    {
        lock (sync)
        {
            return !disposed && currentRunId == runId && generation == runId;
        }
    }

    private bool Invalidate(int runId)
    {
        lock (sync)
        {
            if (currentRunId != runId || generation != runId)
            {
                return false;
            }

            generation++;
            currentRunId = 0;
            graphStarted = false;
            paused = false;
            return true;
        }
    }

    private async Task StopRunSilentlyAsync(int runId)
    {
        try
        {
            await Interop.StopAsync(handle, runId);
        }
        catch
        {
            // Preserve the original playback/start failure during best-effort teardown.
        }
    }

    private static BrowserAudioException ToBrowserException(string message, Exception error)
        => error is BrowserAudioException browserError
            ? browserError
            : new BrowserAudioException(message, error);

    public void Dispose()
    {
        Task<AudioWorkletPreparation> preparation;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            generation++;
            currentRunId = 0;
            graphStarted = false;
            paused = false;
            preparation = preparationTask;
        }

        _ = DisposeGraphAsync(preparation);
    }

    private async Task DisposeGraphAsync(Task<AudioWorkletPreparation> preparation)
    {
        if (preparation == null)
        {
            return;
        }

        try
        {
            try
            {
                await preparation;
            }
            catch
            {
                // A failed prepare may still have created a context that needs closing.
            }

            await EnsureModuleAsync();
            await Interop.DisposeGraphAsync(handle);
        }
        catch
        {
            // IDisposable cannot surface asynchronous graph-close failures.
        }
    }
}
#endif
