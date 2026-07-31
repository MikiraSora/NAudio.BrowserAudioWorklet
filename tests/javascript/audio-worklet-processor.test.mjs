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

function readSharedTotal(sharedBuffer) {
    const state = new Int32Array(sharedBuffer);
    const sequence = Atomics.load(state, 0);
    const low = BigInt(Atomics.load(state, 1) >>> 0);
    const high = BigInt(Atomics.load(state, 2) >>> 0);
    return {
        sequence,
        frameCount: (high << 32n) | low,
    };
}

function snapshotMessages(processor) {
    return processor.port.messages
        .map(entry => entry.message)
        .filter(message => message.type === "consumed-snapshot");
}

function snapshotValue(message) {
    return (BigInt(message.high >>> 0) << 32n) | BigInt(message.low >>> 0);
}

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

test("processor counts stereo frames across blocks and partial quanta without counting underrun silence", () => {
    const processor = new ProcessorType({
        processorOptions: {
            channels: 2,
            consumedFrameState: null,
            initialConsumedLow: 0,
            initialConsumedHigh: 0,
            nodeId: 7,
        },
    });
    processor.port.emit({
        type: "start",
        runId: 11,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });

    // The first block is source silence. It must still count because it is copied source audio,
    // while an unfilled output tail must not count merely because Web Audio emits zeroes there.
    const silentSource = new Float32Array(40 * 2);
    processor.port.emit({
        type: "samples",
        runId: 11,
        buffer: silentSource.buffer,
        sampleCount: silentSource.length,
    });
    const secondBlock = new Float32Array(100 * 2);
    for (let frame = 0; frame < 100; frame++) {
        secondBlock[frame * 2] = frame + 0.25;
        secondBlock[frame * 2 + 1] = -(frame + 0.25);
    }
    processor.port.emit({
        type: "samples",
        runId: 11,
        buffer: secondBlock.buffer,
        sampleCount: secondBlock.length,
    });

    const left = new Float32Array(128);
    const right = new Float32Array(128);
    assert.equal(processor.process([], [[left, right]]), true);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 128n,
        "40 silent stereo frames plus 88 frames from the next block are 128 frames, not 256 samples");
    assert.deepEqual([...left.slice(0, 40)], [...new Float32Array(40)],
        "copied source silence is rendered and counted");
    assert.equal(left[40], 0.25, "the render quantum crosses the transferred-block boundary");
    assert.equal(right[40], -0.25);

    const partialLeft = new Float32Array(128);
    const partialRight = new Float32Array(128);
    assert.equal(processor.process([], [[partialLeft, partialRight]]), true);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 140n,
        "only the 12 copied frames count in a partial quantum");
    assert.equal(partialLeft[11], 99.25);
    assert.equal(partialLeft[12], 0, "the unfilled tail remains underrun silence");

    const silenceLeft = new Float32Array(128);
    const silenceRight = new Float32Array(128);
    assert.equal(processor.process([], [[silenceLeft, silenceRight]]), true);
    const snapshots = snapshotMessages(processor);
    assert.equal(snapshots.length, 3, "fallback mode publishes one exact snapshot per active quantum");
    assert.equal(snapshotValue(snapshots.at(-1)), 140n,
        "a fully empty underrun quantum is not consumed source audio");
});

test("processor preserves cumulative totals across start flush stop and drain and reports the final stop value", () => {
    const processor = new ProcessorType({
        processorOptions: {
            channels: 1,
            consumedFrameState: null,
            initialConsumedLow: 0,
            initialConsumedHigh: 0,
            nodeId: 8,
        },
    });
    processor.port.emit({
        type: "start",
        runId: 21,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });
    const firstSamples = new Float32Array([0.1, 0.2, 0.3, 0.4]);
    processor.port.emit({
        type: "samples",
        runId: 21,
        buffer: firstSamples.buffer,
        sampleCount: firstSamples.length,
    });
    processor.process([], [[new Float32Array(128)]]);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 4n);

    processor.port.emit({ type: "stop", runId: 21 });
    const stopped = processor.port.messages
        .map(entry => entry.message)
        .find(message => message.type === "stopped" && message.runId === 21);
    assert.ok(stopped, "stop acknowledges the final processor-thread value");
    assert.equal(snapshotValue(stopped), 4n);

    processor.port.emit({
        type: "start",
        runId: 22,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });
    const secondSamples = new Float32Array([0.5, 0.6]);
    processor.port.emit({
        type: "samples",
        runId: 22,
        buffer: secondSamples.buffer,
        sampleCount: secondSamples.length,
    });
    processor.process([], [[new Float32Array(128)]]);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 6n,
        "a new start continues the player-lifetime cumulative total");

    processor.port.emit({
        type: "flush",
        runId: 23,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });
    const finalSamples = new Float32Array([0.5, 0.6, 0.7]);
    processor.port.emit({
        type: "samples",
        runId: 23,
        buffer: finalSamples.buffer,
        sampleCount: finalSamples.length,
    });
    processor.port.emit({ type: "drain", runId: 23 });
    processor.process([], [[new Float32Array(128)]]);

    assert.equal(processor.active, false);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 9n,
        "flush and natural drain retain all previously copied frames");
    assert.ok(processor.port.messages.some(
        entry => entry.message.type === "drained" && entry.message.runId === 23));
});

test("processor acknowledges ordered explicit resets and resumes counting from zero", () => {
    const processor = new ProcessorType({
        processorOptions: {
            channels: 1,
            consumedFrameState: null,
            initialConsumedLow: 0,
            initialConsumedHigh: 0,
            nodeId: 9,
        },
    });
    processor.port.emit({
        type: "start",
        runId: 31,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });

    const initialSamples = new Float32Array([0.1, 0.2, 0.3]);
    processor.port.emit({
        type: "samples",
        runId: 31,
        buffer: initialSamples.buffer,
        sampleCount: initialSamples.length,
    });
    processor.process([], [[new Float32Array(128)]]);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 3n);

    processor.port.emit({ type: "reset-consumed", resetId: 41 });
    processor.port.emit({ type: "reset-consumed", resetId: 42 });
    const acknowledgements = processor.port.messages
        .map(entry => entry.message)
        .filter(message => message.type === "consumed-reset");
    assert.deepEqual(acknowledgements.map(message => message.resetId), [41, 42]);
    assert.deepEqual(acknowledgements.map(snapshotValue), [0n, 0n]);

    const afterReset = new Float32Array([0.4, 0.5]);
    processor.port.emit({
        type: "samples",
        runId: 31,
        buffer: afterReset.buffer,
        sampleCount: afterReset.length,
    });
    processor.process([], [[new Float32Array(128)]]);
    assert.equal(snapshotValue(snapshotMessages(processor).at(-1)), 2n);
});

test("processor publishes a stable three-word shared snapshot across the unsigned low-word boundary", () => {
    const consumedFrameState = new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 3);
    const processor = new ProcessorType({
        processorOptions: {
            channels: 1,
            consumedFrameState,
            initialConsumedLow: 0xfffffff0,
            initialConsumedHigh: 0,
            nodeId: 10,
        },
    });
    processor.port.emit({
        type: "start",
        runId: 51,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });

    const samples = new Float32Array(32).fill(0.25);
    processor.port.emit({
        type: "samples",
        runId: 51,
        buffer: samples.buffer,
        sampleCount: samples.length,
    });
    processor.process([], [[new Float32Array(128)]]);

    const state = new Int32Array(consumedFrameState);
    const published = readSharedTotal(consumedFrameState);
    assert.equal(published.frameCount, 0x1_0000_0010n);
    assert.equal(published.sequence % 2, 0, "the sequence is even after the writer completes");
    assert.ok(published.sequence > 0);
    assert.equal(Atomics.load(state, 1) >>> 0, 0x10, "low word wraps without losing frames");
    assert.equal(Atomics.load(state, 2) >>> 0, 0x1, "high word increments at the boundary");
    assert.equal(snapshotMessages(processor).length, 0,
        "shared-memory mode does not also allocate fallback snapshot messages");
});

test("processor saturates the cumulative counter at the signed 64-bit maximum", () => {
    const consumedFrameState = new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT * 3);
    const processor = new ProcessorType({
        processorOptions: {
            channels: 1,
            consumedFrameState,
            initialConsumedLow: 0xfffffff0,
            initialConsumedHigh: 0x7fffffff,
            nodeId: 11,
        },
    });
    processor.port.emit({
        type: "start",
        runId: 61,
        bufferFrameCount: 512,
        initialBufferFrameCount: 128,
        requestInitialBuffer: false,
    });
    const samples = new Float32Array(32).fill(0.5);
    processor.port.emit({
        type: "samples",
        runId: 61,
        buffer: samples.buffer,
        sampleCount: samples.length,
    });

    processor.process([], [[new Float32Array(128)]]);

    assert.equal(readSharedTotal(consumedFrameState).frameCount, 0x7fff_ffff_ffff_ffffn);
});
