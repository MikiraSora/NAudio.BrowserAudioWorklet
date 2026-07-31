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
        this.audioWorklet = { addModule: async () => undefined };
        FakeAudioContext.instances.push(this);
    }

    async resume() {
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

const transportSource = await readFile(
    new URL("../../src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet.js", import.meta.url),
    "utf8");
const transportUrl = new URL(
    "../../src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet.js",
    import.meta.url).href;
const nodeCompatibleTransportSource = transportSource.replace(
    "import.meta.url",
    JSON.stringify(transportUrl));
const transport = await import(
    `data:text/javascript;base64,${Buffer.from(nodeCompatibleTransportSource).toString("base64")}`);

test("transport preserves one graph and accepts an array-like MemoryView", async () => {
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
    const memoryView = { length: bytes.length };
    for (let index = 0; index < bytes.length; index++) {
        memoryView[index] = bytes[index];
    }

    transport.enqueue(handle, 1, memoryView, 2);
    const sampleMessage = node.port.messages.find(entry => entry.message.type === "samples");
    assert.ok(sampleMessage);
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
    transport.enqueue(handle, 1, bytes, 2);
    const sampleMessages = node.port.messages.filter(entry => entry.message.type === "samples");
    assert.equal(sampleMessages[1].message.buffer, firstBuffer);

    await transport.stop(handle, 1);
    assert.equal(FakeAudioContext.instances[0].state, "suspended");

    transport.beginStart(handle, 2, 960, 512, 0);
    await transport.resume(handle);
    assert.equal(FakeAudioContext.instances.length, 1);
    assert.equal(FakeAudioWorkletNode.instances.length, 1);

    transport.flush(handle, 3, 960, 512);
    const flushMessage = node.port.messages.find(
        entry => entry.message.type === "flush" && entry.message.runId === 3);
    assert.equal(flushMessage.message.requestInitialBuffer, true);

    await transport.stop(handle, 3);
    await transport.disposeGraph(handle);
    assert.equal(FakeAudioContext.instances[0].state, "closed");
});
