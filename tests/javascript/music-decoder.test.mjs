import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

let decodedBytes;
globalThis.OfflineAudioContext = class {
    async decodeAudioData(buffer) {
        decodedBytes = new Uint8Array(buffer);
        const channels = [
            new Float32Array([0.25, 0.5]),
            new Float32Array([-0.25, -0.5]),
        ];
        return {
            numberOfChannels: channels.length,
            length: channels[0].length,
            sampleRate: 48000,
            getChannelData: channel => channels[channel],
        };
    }
};

const decoderSource = await readFile(
    new URL("../../samples/BrowserMusicPlayerDemo/wwwroot/music-decoder.js", import.meta.url),
    "utf8");
const decoder = await import(
    `data:text/javascript;base64,${Buffer.from(decoderSource).toString("base64")}`);

test("decoder accepts array-like MemoryViews for compressed input and PCM output", async () => {
    const input = { length: 4, 0: 1, 1: 2, 2: 3, 3: 4 };
    decoder.setFileData(1, input, 4);

    const info = await decoder.decode(1);
    assert.deepEqual(info, { sampleRate: 48000, channels: 2, frames: 2 });
    assert.deepEqual([...decodedBytes], [1, 2, 3, 4]);

    const expected = new Uint8Array(
        new Float32Array([0.25, -0.25, 0.5, -0.5]).buffer);
    const output = { byteLength: expected.byteLength };
    const copied = decoder.copyPcm(1, output, 0);

    assert.equal(copied, expected.byteLength);
    assert.deepEqual(
        Array.from({ length: copied }, (_, index) => output[index]),
        [...expected]);

    decoder.release(1);
    await assert.rejects(() => decoder.decode(1), /unknown handle/);
});
