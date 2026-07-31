using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace BrowserMusicPlayerDemo;

/// <summary>
/// Decodes compressed audio files (mp3/ogg/wav) into interleaved float PCM using the
/// browser's own <c>AudioContext.decodeAudioData</c>. The decoded buffer lives in
/// JavaScript only until it has been copied into managed memory in chunks.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class AudioDecoder
{
    private const string ModuleName = "music-decoder";
    private const string ModuleUrl = "../music-decoder.js";
    private const int CopyChunkBytes = 4 * 1024 * 1024;

    private static Task? moduleLoad;
    private static int nextHandle;

    public static async Task<DecodedAudio> DecodeAsync(byte[] fileBytes, int fileOffset, int count)
    {
        Task load = moduleLoad ??= JSHost.ImportAsync(ModuleName, ModuleUrl);
        try
        {
            await load;
        }
        catch
        {
            if (ReferenceEquals(moduleLoad, load))
            {
                moduleLoad = null;
            }

            throw;
        }

        int handle = Interlocked.Increment(ref nextHandle);
        JSObject? info = null;
        try
        {
            Interop.SetFileData(handle, fileBytes.AsSpan(fileOffset, count), count);
            info = await Interop.DecodeAsync(handle);
            int sampleRate = info.GetPropertyAsInt32("sampleRate");
            int channels = info.GetPropertyAsInt32("channels");
            int frames = info.GetPropertyAsInt32("frames");

            var samples = new float[checked(frames * channels)];
            var destination = MemoryMarshal.AsBytes(samples.AsSpan());
            int pcmOffset = 0;
            while (pcmOffset < destination.Length)
            {
                int chunk = Math.Min(CopyChunkBytes, destination.Length - pcmOffset);
                int copied = Interop.CopyPcm(
                    handle,
                    destination.Slice(pcmOffset, chunk),
                    pcmOffset);
                if (copied != chunk)
                {
                    throw new InvalidOperationException(
                        $"The browser decoder returned {copied} of {chunk} requested PCM bytes.");
                }

                pcmOffset += copied;
            }

            return new DecodedAudio(sampleRate, channels, frames, samples);
        }
        finally
        {
            Interop.Release(handle);
            info?.Dispose();
        }
    }

    private static partial class Interop
    {
        /// <summary>
        /// Hands the compressed file bytes to JavaScript. Synchronous because a memory
        /// view cannot be marshaled on an async import; JavaScript copies the view.
        /// </summary>
        [JSImport("setFileData", ModuleName)]
        public static partial void SetFileData(
            int handle,
            [JSMarshalAs<JSType.MemoryView>] Span<byte> data,
            int byteLength);

        [JSImport("decode", ModuleName)]
        public static partial Task<JSObject> DecodeAsync(int handle);

        [JSImport("copyPcm", ModuleName)]
        public static partial int CopyPcm(
            int handle, [JSMarshalAs<JSType.MemoryView>] Span<byte> destination, int byteOffset);

        [JSImport("release", ModuleName)]
        public static partial void Release(int handle);
    }
}

internal sealed record DecodedAudio(int SampleRate, int Channels, int Frames, float[] Samples);
