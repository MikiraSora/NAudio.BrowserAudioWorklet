#if BROWSER
using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace NAudio.Wave.Browser;

[SupportedOSPlatform("browser")]
internal sealed partial class AudioWorkletBridge
{
    /// <summary>
    /// Calls into the JavaScript module. Import names bind to the exports of
    /// <c>naudio-audio-worklet.js</c> registered under <see cref="ModuleName"/>. Every call is
    /// keyed by the player's handle so concurrent graphs stay isolated.
    /// </summary>
    private static partial class Interop
    {
        [JSImport("prepare", ModuleName)]
        public static partial Task<JSObject> PrepareAsync(
            int handle,
            int requestedSampleRate,
            int channels,
            [JSMarshalAs<JSType.Boolean>] bool useDeviceSampleRate);

        [JSImport("beginStart", ModuleName)]
        public static partial void BeginStart(
            int handle,
            int runId,
            int bufferFrameCount,
            int initialBufferFrameCount,
            double requestLeadTimeSeconds);

        /// <summary>
        /// Resolves when the worklet needs more audio, yielding the requested frame count, or
        /// <c>0</c> when the graph is being torn down.
        /// </summary>
        [JSImport("waitForDemand", ModuleName)]
        public static partial Task<int> WaitForDemandAsync(int handle, int runId);

        /// <summary>
        /// Exposes the bit-identical bytes of interleaved floats as a memory view. JavaScript copies
        /// the view into a recycled transferable buffer owned by the AudioWorklet thread.
        /// </summary>
        [JSImport("enqueue", ModuleName)]
        public static partial void Enqueue(
            int handle,
            int runId,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> data,
            int frameCount);

        [JSImport("waitForEvent", ModuleName)]
        public static partial Task<JSObject> WaitForEventAsync(int handle, int runId);

        /// <summary>Waits for the worklet to play out its buffered frames at end of stream.</summary>
        [JSImport("drain", ModuleName)]
        public static partial Task DrainAsync(int handle, int runId);

        [JSImport("flush", ModuleName)]
        public static partial void Flush(
            int handle,
            int runId,
            int bufferFrameCount,
            int initialBufferFrameCount);

        [JSImport("pause", ModuleName)]
        public static partial Task PauseAsync(int handle);

        [JSImport("resume", ModuleName)]
        public static partial Task ResumeAsync(int handle);

        [JSImport("setVolume", ModuleName)]
        public static partial void SetVolume(int handle, [JSMarshalAs<JSType.Number>] float volume);

        [JSImport("stop", ModuleName)]
        public static partial Task StopAsync(int handle, int runId);

        /// <summary>
        /// Captures one stable 64-bit counter snapshot and returns its low word. The paired high
        /// word is retained by JavaScript so the following call observes the same instant.
        /// </summary>
        [JSImport("captureTotalConsumedFrameCountLow", ModuleName)]
        public static partial int CaptureTotalConsumedFrameCountLow(int handle);

        [JSImport("getCapturedTotalConsumedFrameCountHigh", ModuleName)]
        public static partial int GetCapturedTotalConsumedFrameCountHigh(int handle);

        [JSImport("resetTotalConsumed", ModuleName)]
        public static partial Task ResetTotalConsumedAsync(int handle);

        [JSImport("getMetrics", ModuleName)]
        public static partial JSObject GetMetrics(int handle);

        [JSImport("disposeGraph", ModuleName)]
        public static partial Task DisposeGraphAsync(int handle);
    }
}
#endif
