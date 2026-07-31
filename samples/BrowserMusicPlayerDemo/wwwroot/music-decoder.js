// Browser audio decoding for BrowserMusicPlayerDemo.
// AudioContext.decodeAudioData handles mp3/ogg/wav natively in the browser, so the
// demo does not need a managed codec. Decoded PCM stays in JavaScript until managed
// code pulls it across the boundary in chunks, then the entry is released.

const entries = new Map();

let decodeContext;

function getEntry(handle) {
    const entry = entries.get(handle);
    if (!entry) {
        throw new Error(`Music decoder: unknown handle ${handle}`);
    }
    return entry;
}

// The MemoryView is valid only for this synchronous interop call, so the file bytes
// are copied here before the asynchronous decode starts.
export function setFileData(handle, data, byteLength) {
    const bytes = new Uint8Array(byteLength);
    bytes.set(data);
    entries.set(handle, { bytes, pcm: null });
}

export async function decode(handle) {
    if (typeof globalThis.AudioContext !== "function" &&
        typeof globalThis.OfflineAudioContext !== "function") {
        throw new Error("This browser does not support the Web Audio API.");
    }

    const entry = getEntry(handle);
    decodeContext ??= typeof globalThis.OfflineAudioContext === "function"
        ? new OfflineAudioContext(1, 1, 44100)
        : new AudioContext({ latencyHint: "playback" });
    const audioBuffer = await decodeContext.decodeAudioData(entry.bytes.buffer);
    entry.bytes = null;

    const channels = audioBuffer.numberOfChannels;
    const frames = audioBuffer.length;
    const pcm = new Float32Array(frames * channels);
    const channelData = Array.from(
        { length: channels },
        (_, channel) => audioBuffer.getChannelData(channel));
    if (channels === 1) {
        pcm.set(channelData[0]);
    } else if (channels === 2) {
        const left = channelData[0];
        const right = channelData[1];
        let destination = 0;
        for (let frame = 0; frame < frames; frame++) {
            pcm[destination++] = left[frame];
            pcm[destination++] = right[frame];
        }
    } else {
        let destination = 0;
        for (let frame = 0; frame < frames; frame++) {
            for (let channel = 0; channel < channels; channel++) {
                pcm[destination++] = channelData[channel][frame];
            }
        }
    }

    entry.pcm = pcm;
    return { sampleRate: audioBuffer.sampleRate, channels, frames };
}

export function copyPcm(handle, destination, byteOffset) {
    const entry = getEntry(handle);
    const available = Math.max(0, entry.pcm.byteLength - byteOffset);
    const byteLength = Math.min(destination.byteLength, available);
    const source = new Uint8Array(entry.pcm.buffer, byteOffset, byteLength);
    if (typeof destination.set === "function") {
        destination.set(source);
    } else {
        // Some .NET browser runtimes expose MemoryView as an indexed array-like object.
        for (let index = 0; index < byteLength; index++) {
            destination[index] = source[index];
        }
    }
    return byteLength;
}

export function release(handle) {
    entries.delete(handle);
}
