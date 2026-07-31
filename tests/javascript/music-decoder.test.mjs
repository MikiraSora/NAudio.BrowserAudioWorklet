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

test("decoder copies compressed bytes through MemoryView.copyTo before decoding", async () => {
    const inputBytes = new Uint8Array([1, 2, 3, 4]);
    let inputCopyCount = 0;
    const input = {
        byteLength: inputBytes.byteLength,
        copyTo(destination) {
            inputCopyCount++;
            destination.set(inputBytes);
        },
    };
    decoder.setFileData(1, input, 4);

    const info = await decoder.decode(1);
    assert.deepEqual(info, { sampleRate: 48000, channels: 2, frames: 2 });
    assert.equal(inputCopyCount, 1);
    assert.deepEqual([...decodedBytes], [1, 2, 3, 4]);

    const expected = new Uint8Array(
        new Float32Array([0.25, -0.25, 0.5, -0.5]).buffer);
    const outputBytes = new Uint8Array(expected.byteLength);
    const output = {
        byteLength: outputBytes.byteLength,
        set(source) {
            outputBytes.set(source);
        },
    };
    const copied = decoder.copyPcm(1, output, 0);

    assert.equal(copied, expected.byteLength);
    assert.deepEqual([...outputBytes], [...expected]);

    decoder.release(1);
    await assert.rejects(() => decoder.decode(1), /unknown handle/);
});

test("decoder retains indexed array-like fallbacks", async () => {
    const input = { length: 4, 0: 5, 1: 6, 2: 7, 3: 8 };
    decoder.setFileData(2, input, 4);

    await decoder.decode(2);
    assert.deepEqual([...decodedBytes], [5, 6, 7, 8]);

    const output = { byteLength: Float32Array.BYTES_PER_ELEMENT };
    assert.equal(decoder.copyPcm(2, output, 0), output.byteLength);
    assert.equal(typeof output[0], "number");
    decoder.release(2);
});
