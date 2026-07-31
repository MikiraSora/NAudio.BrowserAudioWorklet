import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

class FakePort {
    constructor() {
        this.messages = [];
        this.onmessage = null;
    }

    postMessage(message, transfer = []) {
        this.messages.push({ message, transfer });
    }

    emit(message) {
        this.onmessage?.({ data: message });
    }
}

globalThis.AudioWorkletProcessor = class {
    constructor() {
        this.port = new FakePort();
    }
};
globalThis.currentTime = 1.25;

let ProcessorType;
globalThis.registerProcessor = (name, processorType) => {
    assert.equal(name, "naudio-block-queue-processor");
    ProcessorType = processorType;
};

const processorSource = await readFile(
    new URL(
        "../../src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet-processor.js",
        import.meta.url),
    "utf8");
await import(`data:text/javascript;base64,${Buffer.from(processorSource).toString("base64")}`);

test("processor consumes transferred blocks in place and supports primed starts", () => {
    const processor = new ProcessorType({ processorOptions: { channels: 2 } });
    processor.port.emit({
        type: "start",
        runId: 1,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });

    assert.equal(processor.port.messages.some(entry => entry.message.type === "need"), false);

    const samples = new Float32Array([
        0.1, -0.1,
        0.2, -0.2,
        0.3, -0.3,
        0.4, -0.4,
    ]);
    processor.port.emit({
        type: "samples",
        runId: 1,
        buffer: samples.buffer,
        sampleCount: samples.length,
    });

    const secondStageNeed = processor.port.messages.find(
        entry => entry.message.type === "need" && entry.message.runId === 1);
    assert.equal(secondStageNeed.message.frames, 508);

    const left = new Float32Array(128);
    const right = new Float32Array(128);
    assert.equal(processor.process([], [[left, right]]), true);
    assert.deepEqual(
        [...left.slice(0, 4)],
        [...new Float32Array([0.1, 0.2, 0.3, 0.4])]);
    assert.deepEqual(
        [...right.slice(0, 4)],
        [...new Float32Array([-0.1, -0.2, -0.3, -0.4])]);

    const firstFrame = processor.port.messages.find(entry => entry.message.type === "first-frame");
    assert.equal(firstFrame.message.runId, 1);
    assert.equal(firstFrame.message.contextTime, 1.25);

    const recycle = processor.port.messages.find(entry => entry.message.type === "recycle");
    assert.equal(recycle.message.buffer, samples.buffer);

    processor.port.emit({
        type: "flush",
        runId: 2,
        bufferFrameCount: 1024,
        initialBufferFrameCount: 256,
        requestInitialBuffer: true,
    });
    const need = processor.port.messages.find(
        entry => entry.message.type === "need" && entry.message.runId === 2);
    assert.equal(need.message.frames, 256);

    processor.port.emit({ type: "stop", runId: 2 });
    assert.equal(processor.active, false);
    assert.equal(processor.runId, 0);
});
