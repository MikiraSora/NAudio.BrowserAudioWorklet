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
        [JSImport("start", ModuleName)]
        public static partial Task StartAsync(int handle, int sampleRate, int channels, int bufferFrameCount);

        /// <summary>
        /// Resolves when the worklet needs more audio, yielding the requested frame count, or
        /// <c>0</c> when the graph is being torn down.
        /// </summary>
        [JSImport("waitForDemand", ModuleName)]
        public static partial Task<int> WaitForDemandAsync(int handle);

        /// <summary>
        /// Exposes interleaved float bytes as a memory view. JavaScript copies the view into a
        /// transferable buffer owned by the AudioWorklet thread.
        /// </summary>
        [JSImport("enqueue", ModuleName)]
        public static partial void Enqueue(
            int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> data, int frameCount);

        /// <summary>Waits for the worklet to play out its buffered frames at end of stream.</summary>
        [JSImport("drain", ModuleName)]
        public static partial Task DrainAsync(int handle);

        [JSImport("pause", ModuleName)]
        public static partial Task PauseAsync(int handle);

        [JSImport("resume", ModuleName)]
        public static partial Task ResumeAsync(int handle);

        [JSImport("setVolume", ModuleName)]
        public static partial void SetVolume(int handle, [JSMarshalAs<JSType.Number>] float volume);

        [JSImport("stop", ModuleName)]
        public static partial Task StopAsync(int handle);
    }
}
#endif
