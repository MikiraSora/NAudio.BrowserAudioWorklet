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
export function setFileData(handle, data) {
    entries.set(handle, { bytes: data.slice(), pcm: null });
}

export async function decode(handle) {
    if (typeof globalThis.AudioContext !== "function") {
        throw new Error("This browser does not support the Web Audio API.");
    }

    const entry = getEntry(handle);
    decodeContext ??= new AudioContext();
    const audioBuffer = await decodeContext.decodeAudioData(entry.bytes.buffer);
    entry.bytes = null;

    const channels = audioBuffer.numberOfChannels;
    const frames = audioBuffer.length;
    const pcm = new Float32Array(frames * channels);
    for (let channel = 0; channel < channels; channel++) {
        const channelData = audioBuffer.getChannelData(channel);
        for (let frame = 0; frame < frames; frame++) {
            pcm[frame * channels + channel] = channelData[frame];
        }
    }

    entry.pcm = pcm;
    return { sampleRate: audioBuffer.sampleRate, channels, frames };
}

export function copyPcm(handle, destination, byteOffset) {
    const entry = getEntry(handle);
    const available = Math.max(0, entry.pcm.byteLength - byteOffset);
    const byteLength = Math.min(destination.byteLength, available);
    destination.set(new Uint8Array(entry.pcm.buffer, byteOffset, byteLength));
    return byteLength;
}

export function release(handle) {
    entries.delete(handle);
}
