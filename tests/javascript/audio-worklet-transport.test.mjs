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

class FakeAudioContext {
    static instances = [];

    constructor(options) {
        this.options = options;
        this.sampleRate = options.sampleRate ?? 48000;
        this.baseLatency = 0.005;
        this.outputLatency = 0.01;
        this.state = "suspended";
        this.destination = {};
        this.currentTime = 0;
        this.onResume = null;
        this.audioWorklet = { addModule: async () => undefined };
        FakeAudioContext.instances.push(this);
    }

    async resume() {
        this.onResume?.();
        this.state = "running";
    }

    async suspend() {
        this.state = "suspended";
    }

    async close() {
        this.state = "closed";
    }

    getOutputTimestamp() {
        // Edge can transiently return zeroes immediately after resume. The transport
        // must use its performance.now() fallback instead of reporting zero latency.
        return { contextTime: 0, performanceTime: 0 };
    }
}

class FakeAudioWorkletNode {
    static instances = [];

    constructor(context, name, options) {
        this.context = context;
        this.name = name;
        this.options = options;
        this.port = new FakePort();
        FakeAudioWorkletNode.instances.push(this);
    }

    connect() {
        return this;
    }

    disconnect() {
    }
}

class FakeGainNode {
    constructor() {
        this.gain = { value: 1 };
    }

    connect() {
        return this;
    }

    disconnect() {
    }
}

globalThis.AudioContext = FakeAudioContext;
globalThis.AudioWorkletNode = FakeAudioWorkletNode;
globalThis.GainNode = FakeGainNode;
let controlledPerformanceNow = 1000;
Object.defineProperty(globalThis, "performance", {
    configurable: true,
    value: { now: () => controlledPerformanceNow },
});
// Node is not a cross-origin isolated browsing context by default. Set the browser capability
// explicitly so this module import exercises the SharedArrayBuffer + Atomics path; fallback tests
// temporarily remove SharedArrayBuffer through createFallbackGraph below.
globalThis.crossOriginIsolated = true;

const transportSource = await readFile(
    new URL("../../src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet.js", import.meta.url),
    "utf8");
const transportUrl = new URL(
    "../../src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet.js",
    import.meta.url).href;
const nodeCompatibleTransportSource = transportSource.replace(
    "import.meta.url",
    JSON.stringify(transportUrl));
let transportModuleGeneration = 0;

async function importTransport() {
    const uniqueSource = `${nodeCompatibleTransportSource}\n// test-module-${++transportModuleGeneration}`;
    return import(`data:text/javascript;base64,${Buffer.from(uniqueSource).toString("base64")}`);
}

function splitTotal(frameCount) {
    const value = BigInt(frameCount);
    return {
        low: Number(value & 0xffffffffn) | 0,
        high: Number((value >> 32n) & 0xffffffffn) | 0,
    };
}

function publishSharedTotal(sharedBuffer, frameCount) {
    const state = new Int32Array(sharedBuffer);
    const { low, high } = splitTotal(frameCount);
    let sequence = Atomics.load(state, 0);
    sequence = (sequence + 1) | 1;
    Atomics.store(state, 0, sequence);
    Atomics.store(state, 1, low);
    Atomics.store(state, 2, high);
    Atomics.store(state, 0, sequence + 1);
}

function readTotal(module, handle) {
    const low = BigInt(module.captureTotalConsumedFrameCountLow(handle) >>> 0);
    const high = BigInt(module.getCapturedTotalConsumedFrameCountHigh(handle) >>> 0);
    return (high << 32n) | low;
}

async function stopWithSnapshot(module, node, handle, runId, frameCount = 0n) {
    const stopTask = module.stop(handle, runId);
    const { low, high } = splitTotal(frameCount);
    node.port.emit({ type: "stopped", runId, low, high });
    await stopTask;
}

async function createFallbackGraph(handle, channels = 1) {
    const originalSharedArrayBuffer = globalThis.SharedArrayBuffer;
    globalThis.SharedArrayBuffer = undefined;
    try {
        const module = await importTransport();
        const nodeIndex = FakeAudioWorkletNode.instances.length;
        await module.prepare(handle, 48000, channels, true);
        return { module, node: FakeAudioWorkletNode.instances[nodeIndex] };
    } finally {
        globalThis.SharedArrayBuffer = originalSharedArrayBuffer;
    }
}

const transport = await importTransport();

test("transport copies MemoryView bytes before transferring sample blocks", async () => {
    const handle = 101;
    const preparation = await transport.prepare(handle, 44100, 2, true);
    const secondPreparation = await transport.prepare(handle, 44100, 2, true);

    assert.equal(FakeAudioContext.instances.length, 1);
    assert.equal(FakeAudioWorkletNode.instances.length, 1);
    assert.equal(preparation.sampleRate, 48000);
    assert.deepEqual(secondPreparation, preparation);

    const node = FakeAudioWorkletNode.instances[0];
    transport.beginStart(handle, 1, 960, 512, 0.004);

    const bytes = new Uint8Array(new Float32Array([0.25, -0.25, 0.5, -0.5]).buffer);
    let copyCount = 0;
    const memoryView = {
        byteLength: bytes.byteLength,
        copyTo(destination) {
            copyCount++;
            destination.set(bytes);
        },
    };

    transport.enqueue(handle, 1, memoryView, 2);
    const sampleMessage = node.port.messages.find(entry => entry.message.type === "samples");
    assert.ok(sampleMessage);
    assert.equal(copyCount, 1);
    assert.deepEqual(
        [...new Uint8Array(sampleMessage.message.buffer, 0, bytes.length)],
        [...bytes]);

    const demand = transport.waitForDemand(handle, 1);
    node.port.emit({ type: "need", runId: 1, frames: 448 });
    assert.equal(await demand, 448);

    const firstFrame = transport.waitForEvent(handle, 1);
    node.port.emit({ type: "first-frame", runId: 1, contextTime: 0 });
    const firstFrameEvent = await firstFrame;
    assert.equal(firstFrameEvent.type, "first-frame");
    assert.ok(firstFrameEvent.startToOutputLatency > 0);

    const metrics = transport.getMetrics(handle);
    assert.equal(metrics.hasFirstFrame, true);
    assert.ok(metrics.startToOutputLatencySeconds > 0);

    const firstBuffer = sampleMessage.message.buffer;
    node.port.emit({ type: "recycle", buffer: firstBuffer });
    const fallbackBytes = new Uint8Array(
        new Float32Array([0.75, -0.75, 0.125, -0.125]).buffer);
    transport.enqueue(handle, 1, fallbackBytes, 2);
    const sampleMessages = node.port.messages.filter(entry => entry.message.type === "samples");
    assert.equal(sampleMessages[1].message.buffer, firstBuffer);
    assert.deepEqual(
        [...new Uint8Array(sampleMessages[1].message.buffer, 0, fallbackBytes.length)],
        [...fallbackBytes]);

    await stopWithSnapshot(transport, node, handle, 1);
    assert.equal(FakeAudioContext.instances[0].state, "suspended");

    transport.beginStart(handle, 2, 960, 512, 0);
    await transport.resume(handle);
    assert.equal(FakeAudioContext.instances.length, 1);
    assert.equal(FakeAudioWorkletNode.instances.length, 1);

    transport.flush(handle, 3, 960, 512);
    const flushMessage = node.port.messages.find(
        entry => entry.message.type === "flush" && entry.message.runId === 3);
    assert.equal(flushMessage.message.requestInitialBuffer, true);

    await stopWithSnapshot(transport, node, handle, 3);
    await transport.disposeGraph(handle);
    assert.equal(FakeAudioContext.instances[0].state, "closed");
});

test("transport shared-memory path captures exact 64-bit totals and resets only its baseline", async () => {
    const handle = 202;
    const nodeIndex = FakeAudioWorkletNode.instances.length;
    await transport.prepare(handle, 48000, 2, true);
    const node = FakeAudioWorkletNode.instances[nodeIndex];
    const sharedBuffer = node.options.processorOptions.consumedFrameState;

    assert.ok(sharedBuffer instanceof SharedArrayBuffer);
    assert.equal(sharedBuffer.byteLength, Int32Array.BYTES_PER_ELEMENT * 3);
    assert.equal(node.options.processorOptions.initialConsumedLow, 0);
    assert.equal(node.options.processorOptions.initialConsumedHigh, 0);
    assert.ok(Number.isInteger(node.options.processorOptions.nodeId));

    transport.beginStart(handle, 17, 960, 512, 0);
    const firstRawTotal = 0x1_0000_0201n;
    publishSharedTotal(sharedBuffer, firstRawTotal);

    const capturedLow = transport.captureTotalConsumedFrameCountLow(handle);
    publishSharedTotal(sharedBuffer, 0x2_0000_0302n);
    const capturedHigh = transport.getCapturedTotalConsumedFrameCountHigh(handle);
    assert.equal(
        (BigInt(capturedHigh >>> 0) << 32n) | BigInt(capturedLow >>> 0),
        firstRawTotal,
        "low and high exports expose the same stable capture even if the writer advances between calls");
    assert.equal(readTotal(transport, handle), 0x2_0000_0302n);

    const sharedState = new Int32Array(sharedBuffer);
    const stableSequence = Atomics.load(sharedState, 0);
    const inProgressTotal = 0x3_0000_0403n;
    const inProgressWords = splitTotal(inProgressTotal);
    Atomics.store(sharedState, 0, stableSequence + 1);
    Atomics.store(sharedState, 1, inProgressWords.low);
    Atomics.store(sharedState, 2, inProgressWords.high);
    assert.equal(readTotal(transport, handle), 0x2_0000_0302n,
        "a bounded read of an odd in-progress sequence falls back to the last stable snapshot");
    Atomics.store(sharedState, 0, stableSequence + 2);
    assert.equal(readTotal(transport, handle), inProgressTotal);

    const resetMessageCount = node.port.messages.filter(
        entry => entry.message.type === "reset-consumed").length;
    await transport.resetTotalConsumed(handle);
    assert.equal(readTotal(transport, handle), 0n);
    assert.equal(
        node.port.messages.filter(entry => entry.message.type === "reset-consumed").length,
        resetMessageCount,
        "shared-memory reset updates the main-thread baseline without messaging the audio thread");

    const resetRawTotal = inProgressTotal;
    publishSharedTotal(sharedBuffer, resetRawTotal + 33n);
    assert.equal(readTotal(transport, handle), 33n);

    transport.flush(handle, 18, 960, 512);
    assert.equal(readTotal(transport, handle), 33n, "flush does not reset the cumulative baseline");

    await stopWithSnapshot(transport, node, handle, 18, resetRawTotal + 33n);
    assert.equal(readTotal(transport, handle), 33n, "stop retains the final total");

    transport.beginStart(handle, 19, 960, 512, 0);
    assert.equal(readTotal(transport, handle), 33n, "a new run retains player-lifetime progress");
    await stopWithSnapshot(transport, node, handle, 19, resetRawTotal + 33n);
    await transport.disposeGraph(handle);
});

test("transport fallback snapshots remain exact and stop waits for the final processor value", async () => {
    const handle = 303;
    const { module: fallbackTransport, node } = await createFallbackGraph(handle);
    assert.equal(node.options.processorOptions.consumedFrameState, null);
    assert.equal(node.options.processorOptions.initialConsumedLow, 0);
    assert.equal(node.options.processorOptions.initialConsumedHigh, 0);

    fallbackTransport.beginStart(handle, 1, 960, 512, 0);
    assert.ok(node.port.messages.some(entry => entry.message.type === "start"),
        "fallback telemetry must not disable playback");

    const firstTotal = 0x1_0000_0201n;
    node.port.emit({ type: "consumed-snapshot", ...splitTotal(firstTotal) });
    for (let index = 0; index < 1024; index++) {
        assert.equal(readTotal(fallbackTransport, handle), firstTotal,
            "fallback getters return the last confirmed snapshot without clock interpolation");
    }

    let stopResolved = false;
    const stopTask = fallbackTransport.stop(handle, 1).then(() => {
        stopResolved = true;
    });
    await Promise.resolve();
    assert.equal(stopResolved, false, "fallback stop waits for the processor's final snapshot");

    const finalTotal = firstTotal + 17n;
    node.port.emit({ type: "stopped", runId: 1, ...splitTotal(finalTotal) });
    await stopTask;
    assert.equal(readTotal(fallbackTransport, handle), finalTotal);

    fallbackTransport.beginStart(handle, 2, 960, 512, 0);
    assert.equal(readTotal(fallbackTransport, handle), finalTotal, "new start retains the confirmed total");
    await stopWithSnapshot(fallbackTransport, node, handle, 2, finalTotal);
    await fallbackTransport.disposeGraph(handle);
});

test("transport fallback orders concurrent resets and resolves each matching acknowledgement", async () => {
    const handle = 404;
    const { module: fallbackTransport, node } = await createFallbackGraph(handle);
    fallbackTransport.beginStart(handle, 1, 960, 512, 0);
    node.port.emit({ type: "consumed-snapshot", ...splitTotal(10n) });

    let secondResolved = false;
    const firstReset = fallbackTransport.resetTotalConsumed(handle);
    const secondReset = fallbackTransport.resetTotalConsumed(handle).then(() => {
        secondResolved = true;
    });

    let resetMessages = node.port.messages
        .map(entry => entry.message)
        .filter(message => message.type === "reset-consumed");
    assert.ok(resetMessages.length >= 1);
    const firstResetId = resetMessages[0].resetId;
    node.port.emit({
        type: "consumed-reset",
        resetId: firstResetId,
        ...splitTotal(0n),
    });
    await firstReset;
    await Promise.resolve();

    resetMessages = node.port.messages
        .map(entry => entry.message)
        .filter(message => message.type === "reset-consumed");
    assert.equal(resetMessages.length, 2, "both concurrent reset requests reach the processor");
    assert.ok(resetMessages[1].resetId > firstResetId, "reset sequence numbers increase monotonically");
    assert.equal(secondResolved, false, "the first acknowledgement cannot complete the second reset");

    node.port.emit({
        type: "consumed-reset",
        resetId: resetMessages[1].resetId,
        ...splitTotal(0n),
    });
    await secondReset;
    assert.equal(readTotal(fallbackTransport, handle), 0n);

    node.port.emit({ type: "consumed-snapshot", ...splitTotal(3n) });
    assert.equal(readTotal(fallbackTransport, handle), 3n,
        "consumption after the last reset starts again from zero");
    await stopWithSnapshot(fallbackTransport, node, handle, 1, 3n);
    await fallbackTransport.disposeGraph(handle);
});

test("transport measures resume-to-first-frame latency per run and ignores stale nodes", async () => {
    const handle = 606;
    const nodeIndex = FakeAudioWorkletNode.instances.length;
    await transport.prepare(handle, 48000, 2, true);
    const firstNode = FakeAudioWorkletNode.instances[nodeIndex];
    const context = FakeAudioContext.instances[FakeAudioContext.instances.length - 1];
    context.onResume = () => {
        controlledPerformanceNow += 5;
    };

    controlledPerformanceNow = 100;
    transport.beginStart(handle, 1, 960, 512, 0);
    controlledPerformanceNow = 125;
    await transport.resume(handle);
    const firstEvent = transport.waitForEvent(handle, 1);
    controlledPerformanceNow = 137.5;
    firstNode.port.emit({ type: "first-frame", runId: 1, contextTime: 0 });
    const observedFirstEvent = await firstEvent;
    assert.equal(observedFirstEvent.observedResumeToFirstFrameLatency, 12.5);
    assert.ok(Math.abs(observedFirstEvent.startToOutputLatency - 0.0525) < 1e-12,
        "the existing request-to-estimated-output metric keeps its original boundary");
    assert.ok(Math.abs(transport.getMetrics(handle).startToOutputLatencySeconds - 0.0525) < 1e-12);

    controlledPerformanceNow = 200;
    transport.beginStart(handle, 2, 960, 512, 0);
    controlledPerformanceNow = 210;
    await transport.resume(handle);
    const secondEvent = transport.waitForEvent(handle, 2);
    let secondResolved = false;
    secondEvent.then(() => {
        secondResolved = true;
    });
    controlledPerformanceNow = 211;
    firstNode.port.emit({ type: "first-frame", runId: 1, contextTime: 0 });
    await Promise.resolve();
    assert.equal(secondResolved, false, "a previous run cannot complete the current run");
    controlledPerformanceNow = 222;
    firstNode.port.emit({ type: "first-frame", runId: 2, contextTime: 0 });
    assert.equal((await secondEvent).observedResumeToFirstFrameLatency, 12);

    firstNode.onprocessorerror();
    const replacementIndex = FakeAudioWorkletNode.instances.length;
    controlledPerformanceNow = 300;
    transport.beginStart(handle, 3, 960, 512, 0);
    const replacementNode = FakeAudioWorkletNode.instances[replacementIndex];
    controlledPerformanceNow = 305;
    await transport.resume(handle);
    const thirdEvent = transport.waitForEvent(handle, 3);
    let thirdResolved = false;
    thirdEvent.then(() => {
        thirdResolved = true;
    });
    controlledPerformanceNow = 306;
    firstNode.port.emit({ type: "first-frame", runId: 3, contextTime: 0 });
    await Promise.resolve();
    assert.equal(thirdResolved, false, "a replaced node cannot publish into the new graph");
    controlledPerformanceNow = 310;
    replacementNode.port.emit({ type: "first-frame", runId: 3, contextTime: 0 });
    assert.equal((await thirdEvent).observedResumeToFirstFrameLatency, 5);

    await stopWithSnapshot(transport, replacementNode, handle, 3);
    await transport.disposeGraph(handle);
});

test("transport isolates stale node snapshots and rejects pending resets on failure or disposal", async () => {
    const handle = 505;
    const { module: fallbackTransport, node: firstNode } = await createFallbackGraph(handle);
    fallbackTransport.beginStart(handle, 1, 960, 512, 0);
    firstNode.port.emit({ type: "consumed-snapshot", ...splitTotal(5n) });

    const failedReset = fallbackTransport.resetTotalConsumed(handle);
    firstNode.onprocessorerror();
    await assert.rejects(failedReset, /audio-thread error/);

    const replacementIndex = FakeAudioWorkletNode.instances.length;
    fallbackTransport.beginStart(handle, 2, 960, 512, 0);
    const replacementNode = FakeAudioWorkletNode.instances[replacementIndex];
    assert.notEqual(replacementNode, firstNode);
    assert.equal(replacementNode.options.processorOptions.initialConsumedLow, 5);
    assert.equal(replacementNode.options.processorOptions.initialConsumedHigh, 0);

    firstNode.port.emit({ type: "consumed-snapshot", ...splitTotal(999n) });
    assert.equal(readTotal(fallbackTransport, handle), 5n,
        "messages from the replaced node are ignored by its stale nodeId closure");
    replacementNode.port.emit({ type: "consumed-snapshot", ...splitTotal(7n) });
    assert.equal(readTotal(fallbackTransport, handle), 7n);

    let replacementResetResolved = false;
    const replacementReset = fallbackTransport.resetTotalConsumed(handle).then(() => {
        replacementResetResolved = true;
    });
    const replacementResetMessage = replacementNode.port.messages
        .map(entry => entry.message)
        .findLast(message => message.type === "reset-consumed");
    firstNode.port.emit({
        type: "consumed-reset",
        resetId: replacementResetMessage.resetId,
        ...splitTotal(0n),
    });
    await Promise.resolve();
    assert.equal(replacementResetResolved, false,
        "a stale node cannot acknowledge a reset issued to its replacement");
    replacementNode.port.emit({
        type: "consumed-reset",
        resetId: replacementResetMessage.resetId,
        ...splitTotal(0n),
    });
    await replacementReset;
    assert.equal(readTotal(fallbackTransport, handle), 0n);

    const disposedReset = fallbackTransport.resetTotalConsumed(handle);
    const disposeTask = fallbackTransport.disposeGraph(handle);
    await assert.rejects(disposedReset, /disposed/i);
    await disposeTask;
});
