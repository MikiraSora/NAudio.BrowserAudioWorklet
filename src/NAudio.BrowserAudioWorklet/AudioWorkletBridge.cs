#if BROWSER
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace NAudio.Wave.Browser;

/// <summary>
/// Drives a Web Audio graph through <see cref="System.Runtime.InteropServices.JavaScript"/>
/// interop and feeds it on demand from the managed source.
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
    private int generation;
    private int currentHandle;
    private bool graphStarted;
    private bool paused;
    private float volume = 1.0f;

    private static Task EnsureModuleAsync()
    {
        lock (ModuleLock)
        {
            return moduleLoad ??= System.Runtime.InteropServices.JavaScript.JSHost.ImportAsync(ModuleName, ModuleUrl);
        }
    }

    public async Task StartAsync(
        int sampleRate,
        int channels,
        int bufferFrameCount,
        AudioRenderCallback renderFrames,
        Action<Exception> onStopped)
    {
        ArgumentNullException.ThrowIfNull(renderFrames);

        int handle = Interlocked.Increment(ref nextHandle);
        int runGeneration;
        lock (sync)
        {
            runGeneration = ++generation;
            currentHandle = handle;
            graphStarted = false;
            paused = false;
        }

        try
        {
            await EnsureModuleAsync();
            if (!IsCurrent(handle, runGeneration))
            {
                return;
            }

            await Interop.StartAsync(handle, sampleRate, channels, bufferFrameCount);
            if (!IsCurrent(handle, runGeneration))
            {
                await StopHandleSilentlyAsync(handle);
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

            _ = FeedLoopAsync(
                handle,
                runGeneration,
                channels,
                renderFrames,
                onStopped);
        }
        catch (Exception ex)
        {
            await StopHandleSilentlyAsync(handle);
            if (IsCurrent(handle, runGeneration))
            {
                Invalidate(handle, runGeneration);
                throw ToBrowserException("The browser audio graph could not be created.", ex);
            }
        }
    }

    private async Task FeedLoopAsync(
        int handle,
        int runGeneration,
        int channels,
        AudioRenderCallback renderFrames,
        Action<Exception> onStopped)
    {
        byte[] renderBuffer = Array.Empty<byte>();
        Exception error = null;
        try
        {
            while (IsCurrent(handle, runGeneration))
            {
                int framesNeeded = await Interop.WaitForDemandAsync(handle);
                if (framesNeeded <= 0 || !IsCurrent(handle, runGeneration))
                {
                    return;
                }

                int requiredBytes = checked(framesNeeded * channels * sizeof(float));
                if (renderBuffer.Length < requiredBytes)
                {
                    renderBuffer = new byte[requiredBytes];
                }

                int framesRendered = renderFrames(renderBuffer, framesNeeded);
                if (framesRendered <= 0)
                {
                    await Interop.DrainAsync(handle);
                    break;
                }

                if (framesRendered > framesNeeded)
                {
                    throw new InvalidOperationException("The audio renderer returned more frames than requested.");
                }

                Interop.Enqueue(
                    handle,
                    renderBuffer.AsSpan(0, checked(framesRendered * channels * sizeof(float))),
                    framesRendered);
            }
        }
        catch (Exception ex)
        {
            error = ToBrowserException("Browser audio playback failed.", ex);
        }

        await FinishRunAsync(handle, runGeneration, onStopped, error);
    }

    public Task PauseAsync()
    {
        int handle;
        bool canCallInterop;
        lock (sync)
        {
            paused = true;
            handle = currentHandle;
            canCallInterop = graphStarted && handle != 0;
        }

        return canCallInterop ? Interop.PauseAsync(handle) : Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        int handle;
        bool canCallInterop;
        lock (sync)
        {
            paused = false;
            handle = currentHandle;
            canCallInterop = graphStarted && handle != 0;
        }

        return canCallInterop ? Interop.ResumeAsync(handle) : Task.CompletedTask;
    }

    public void SetVolume(float newVolume)
    {
        int handle;
        bool canCallInterop;
        lock (sync)
        {
            volume = newVolume;
            handle = currentHandle;
            canCallInterop = graphStarted && handle != 0;
        }

        if (canCallInterop)
        {
            Interop.SetVolume(handle, newVolume);
        }
    }

    public async Task StopAsync()
    {
        int handle;
        lock (sync)
        {
            handle = currentHandle;
            generation++;
            currentHandle = 0;
            graphStarted = false;
            paused = false;
        }

        if (handle == 0)
        {
            return;
        }

        await EnsureModuleAsync();
        await Interop.StopAsync(handle);
    }

    private async Task FinishRunAsync(
        int handle,
        int runGeneration,
        Action<Exception> onStopped,
        Exception error)
    {
        if (!Invalidate(handle, runGeneration))
        {
            return;
        }

        await StopHandleSilentlyAsync(handle);
        onStopped?.Invoke(error);
    }

    private bool IsCurrent(int handle, int runGeneration)
    {
        lock (sync)
        {
            return currentHandle == handle && generation == runGeneration;
        }
    }

    private bool Invalidate(int handle, int runGeneration)
    {
        lock (sync)
        {
            if (currentHandle != handle || generation != runGeneration)
            {
                return false;
            }

            generation++;
            currentHandle = 0;
            graphStarted = false;
            paused = false;
            return true;
        }
    }

    private static async Task StopHandleSilentlyAsync(int handle)
    {
        try
        {
            await Interop.StopAsync(handle);
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
        => _ = StopOnDisposeAsync();

    private async Task StopOnDisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        catch
        {
            // IDisposable cannot surface asynchronous teardown failures. StopAsync invalidates
            // the managed run before its first await, so the feed loop cannot continue.
        }
    }
}
#endif
