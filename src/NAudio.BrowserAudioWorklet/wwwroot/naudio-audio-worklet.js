// Main-thread Web Audio transport for NAudio.BrowserAudioWorklet.
// Each player owns one persistent AudioContext and AudioWorkletNode. Playback runs only reset the
// processor queue, which keeps stop/replay and seek off the graph-construction path.

const graphs = new Map();
const MAX_RECYCLED_BUFFERS = 4;
const CONSUMED_FRAME_STATE_WORDS = 3;

function getGraph(handle) {
    const graph = graphs.get(handle);
    if (!graph) {
        throw new Error(`NAudio AudioWorklet: unknown handle ${handle}`);
    }
    return graph;
}

function processorUrl() {
    return new URL("./naudio-audio-worklet-processor.js", import.meta.url).href;
}

function latencyInfo(graph) {
    return {
        sampleRate: graph.context.sampleRate,
        baseLatency: Number.isFinite(graph.context.baseLatency) ? graph.context.baseLatency : 0,
        outputLatency: Number.isFinite(graph.context.outputLatency) ? graph.context.outputLatency : 0,
    };
}

function supportsSharedConsumedFrameState() {
    return globalThis.crossOriginIsolated === true &&
        typeof globalThis.SharedArrayBuffer === "function" &&
        typeof globalThis.Atomics === "object";
}

function readSharedConsumedFrameState(state, fallback) {
    // A writer only holds the odd sequence for three atomic stores. Keep the synchronous getter
    // bounded as well, so a processor that failed mid-write cannot hang the WebAssembly thread.
    for (let attempt = 0; attempt < 1024; attempt++) {
        const sequenceBefore = Atomics.load(state, 0);
        if ((sequenceBefore & 1) !== 0) {
            continue;
        }

        const low = Atomics.load(state, 1) >>> 0;
        const high = Atomics.load(state, 2) >>> 0;
        const sequenceAfter = Atomics.load(state, 0);
        if (sequenceBefore === sequenceAfter && (sequenceAfter & 1) === 0) {
            return { low, high };
        }
    }

    return fallback;
}

function currentConsumedFrameState(graph) {
    if (graph.consumedFrameState) {
        const snapshot = readSharedConsumedFrameState(graph.consumedFrameState, {
            low: graph.consumedSnapshotLow,
            high: graph.consumedSnapshotHigh,
        });
        graph.consumedSnapshotLow = snapshot.low;
        graph.consumedSnapshotHigh = snapshot.high;
    }

    return {
        low: graph.consumedSnapshotLow,
        high: graph.consumedSnapshotHigh,
    };
}

function subtractConsumedFrameState(value, baseline) {
    const borrow = value.low < baseline.low ? 1 : 0;
    return {
        low: (value.low - baseline.low) >>> 0,
        high: (value.high - baseline.high - borrow) >>> 0,
    };
}

function updateConsumedSnapshot(graph, message) {
    graph.consumedSnapshotLow = message.low >>> 0;
    graph.consumedSnapshotHigh = message.high >>> 0;
}

function rejectPendingConsumedResets(graph, error) {
    for (const pending of graph.pendingConsumedResets.values()) {
        pending.reject(error);
    }
    graph.pendingConsumedResets.clear();
}

function rejectPendingStops(graph, error) {
    for (const pending of graph.pendingStops.values()) {
        pending.reject(error);
    }
    graph.pendingStops.clear();
}

function resolveDemandAsStopped(graph) {
    graph.pendingDemand = null;
    if (graph.resolveDemand) {
        graph.resolveDemand(0);
        graph.resolveDemand = null;
        graph.rejectDemand = null;
    }
}

function resolveEventsAsStopped(graph) {
    graph.pendingEvents.length = 0;
    if (graph.resolveEvent) {
        graph.resolveEvent({ type: "stopped" });
        graph.resolveEvent = null;
        graph.rejectEvent = null;
    }
}

function resolveDrainAsStopped(graph) {
    if (graph.drainResolve) {
        graph.drainResolve();
        graph.drainResolve = null;
        graph.drainReject = null;
    }
}

function closeGraph(graph) {
    graph.disposed = true;
    graph.runId = 0;
    const disposalError = new Error("The browser audio graph has been disposed.");
    rejectPendingConsumedResets(graph, disposalError);
    rejectPendingStops(graph, disposalError);
    resolveDemandAsStopped(graph);
    resolveEventsAsStopped(graph);
    resolveDrainAsStopped(graph);

    if (graph.node) {
        graph.node.port.postMessage({ type: "dispose" });
        graph.node.disconnect();
        graph.node = null;
    }
    if (graph.gain) {
        graph.gain.disconnect();
        graph.gain = null;
    }

    graph.recycledBuffers.length = 0;
    return graph.context.close().catch(() => undefined);
}

function failGraph(graph, error) {
    if (graph.error) {
        return;
    }

    graph.error = error instanceof Error ? error : new Error(String(error));
    rejectPendingConsumedResets(graph, graph.error);
    rejectPendingStops(graph, graph.error);
    if (graph.rejectDemand) {
        graph.rejectDemand(graph.error);
        graph.resolveDemand = null;
        graph.rejectDemand = null;
    }
    if (graph.rejectEvent) {
        graph.rejectEvent(graph.error);
        graph.resolveEvent = null;
        graph.rejectEvent = null;
    }
    if (graph.drainReject) {
        graph.drainReject(graph.error);
        graph.drainResolve = null;
        graph.drainReject = null;
    }
}

function createNode(graph) {
    const initialConsumed = currentConsumedFrameState(graph);
    const nodeId = ++graph.nodeId;
    if (graph.useSharedConsumedFrameState) {
        const sharedBuffer = new SharedArrayBuffer(
            Int32Array.BYTES_PER_ELEMENT * CONSUMED_FRAME_STATE_WORDS);
        graph.consumedFrameState = new Int32Array(sharedBuffer);
        Atomics.store(graph.consumedFrameState, 0, 0);
        Atomics.store(graph.consumedFrameState, 1, initialConsumed.low | 0);
        Atomics.store(graph.consumedFrameState, 2, initialConsumed.high | 0);
    } else {
        graph.consumedFrameState = null;
    }

    const node = new AudioWorkletNode(graph.context, "naudio-block-queue-processor", {
        numberOfInputs: 0,
        numberOfOutputs: 1,
        outputChannelCount: [graph.channels],
        processorOptions: {
            channels: graph.channels,
            nodeId,
            consumedFrameState: graph.consumedFrameState?.buffer ?? null,
            initialConsumedLow: initialConsumed.low | 0,
            initialConsumedHigh: initialConsumed.high | 0,
        },
    });
    node.port.onmessage = (event) => onProcessorMessage(graph, nodeId, event.data);
    node.onprocessorerror = () => failGraph(
        graph,
        new Error("The AudioWorklet processor stopped because of an audio-thread error."));
    node.connect(graph.gain);
    graph.node = node;
}

function replaceFailedNode(graph) {
    if (!graph.error) {
        return;
    }

    if (graph.node) {
        graph.node.disconnect();
    }
    graph.error = null;
    createNode(graph);
}

export async function prepare(handle, requestedSampleRate, channels, useDeviceSampleRate) {
    if (typeof globalThis.AudioContext !== "function" ||
        typeof globalThis.AudioWorkletNode !== "function") {
        throw new Error("This browser does not support the Web Audio AudioWorklet API.");
    }

    const existing = graphs.get(handle);
    if (existing) {
        if (existing.channels !== channels) {
            throw new Error("A prepared AudioWorklet graph cannot change its channel count.");
        }
        await existing.preparationPromise;
        return latencyInfo(existing);
    }

    const contextOptions = { latencyHint: "interactive" };
    if (!useDeviceSampleRate) {
        contextOptions.sampleRate = requestedSampleRate;
    }

    const context = new AudioContext(contextOptions);
    const graph = {
        context,
        channels,
        node: null,
        gain: new GainNode(context, { gain: 1.0 }),
        preparationPromise: null,
        runId: 0,
        resumeRunId: 0,
        resumePerformanceTime: 0,
        pendingDemand: null,
        resolveDemand: null,
        rejectDemand: null,
        pendingEvents: [],
        resolveEvent: null,
        rejectEvent: null,
        drainResolve: null,
        drainReject: null,
        nodeId: 0,
        useSharedConsumedFrameState: supportsSharedConsumedFrameState(),
        consumedFrameState: null,
        consumedSnapshotLow: 0,
        consumedSnapshotHigh: 0,
        consumedBaselineLow: 0,
        consumedBaselineHigh: 0,
        capturedConsumedLow: 0,
        capturedConsumedHigh: 0,
        nextConsumedResetId: 0,
        pendingConsumedResets: new Map(),
        pendingStops: new Map(),
        recycledBuffers: [],
        metrics: createMetrics(),
        error: null,
        disposed: false,
    };
    graphs.set(handle, graph);

    graph.preparationPromise = (async () => {
        try {
            await context.audioWorklet.addModule(processorUrl());
            createNode(graph);
            graph.gain.connect(context.destination);
            if (context.state === "running") {
                await context.suspend();
            }
        } catch (error) {
            graphs.delete(handle);
            await closeGraph(graph);
            throw error;
        }
    })();

    await graph.preparationPromise;
    return latencyInfo(graph);
}

function createMetrics() {
    return {
        underrunCount: 0,
        underrunFrames: 0,
        hasFirstFrame: false,
        firstFrameContextTime: 0,
        hasStartToOutputLatency: false,
        startToOutputLatencySeconds: 0,
    };
}

function beginRun(
    graph,
    runId,
    bufferFrameCount,
    initialBufferFrameCount,
    requestLeadTimeSeconds,
    requestInitialBuffer,
    messageType) {
    resolveDemandAsStopped(graph);
    resolveEventsAsStopped(graph);
    resolveDrainAsStopped(graph);
    graph.runId = runId;
    graph.resumeRunId = 0;
    graph.resumePerformanceTime = 0;
    graph.runStartPerformanceTime = performance.now() -
        Math.max(0, requestLeadTimeSeconds * 1000);
    graph.metrics = createMetrics();
    graph.node.port.postMessage({
        type: messageType,
        runId,
        bufferFrameCount,
        initialBufferFrameCount,
        requestInitialBuffer,
    });
}

export function beginStart(
    handle,
    runId,
    bufferFrameCount,
    initialBufferFrameCount,
    requestLeadTimeSeconds) {
    const graph = getGraph(handle);
    if (graph.disposed) {
        throw new Error("The browser audio graph has been disposed.");
    }

    replaceFailedNode(graph);
    beginRun(
        graph,
        runId,
        bufferFrameCount,
        initialBufferFrameCount,
        requestLeadTimeSeconds,
        false,
        "start");
}

export function flush(handle, runId, bufferFrameCount, initialBufferFrameCount) {
    const graph = getGraph(handle);
    if (graph.error) {
        throw graph.error;
    }
    beginRun(graph, runId, bufferFrameCount, initialBufferFrameCount, 0, true, "flush");
}

function estimateOutputPerformanceTime(graph, contextTime) {
    if (typeof graph.context.getOutputTimestamp === "function") {
        const timestamp = graph.context.getOutputTimestamp();
        if (Number.isFinite(timestamp.contextTime) &&
            Number.isFinite(timestamp.performanceTime) &&
            timestamp.performanceTime > 0) {
            const estimatedPerformanceTime = timestamp.performanceTime +
                (contextTime - timestamp.contextTime) * 1000;
            if (estimatedPerformanceTime >= graph.runStartPerformanceTime) {
                return estimatedPerformanceTime;
            }
        }
    }

    const latency = latencyInfo(graph);
    return performance.now() +
        (latency.baseLatency + latency.outputLatency) * 1000;
}

function enqueueEvent(graph, event) {
    if (graph.resolveEvent) {
        const resolve = graph.resolveEvent;
        graph.resolveEvent = null;
        graph.rejectEvent = null;
        resolve(event);
    } else {
        graph.pendingEvents.push(event);
    }
}

function recycleBuffer(graph, buffer) {
    if (graph.recycledBuffers.length < MAX_RECYCLED_BUFFERS) {
        graph.recycledBuffers.push(buffer);
    }
}

function onProcessorMessage(graph, nodeId, message) {
    const observedPerformanceTime = message.type === "first-frame" ? performance.now() : 0;
    if (graph.disposed || nodeId !== graph.nodeId) {
        return;
    }

    if (message.type === "consumed-snapshot") {
        updateConsumedSnapshot(graph, message);
        return;
    }

    if (message.type === "consumed-reset") {
        updateConsumedSnapshot(graph, message);
        const pending = graph.pendingConsumedResets.get(message.resetId);
        if (pending) {
            graph.pendingConsumedResets.delete(message.resetId);
            pending.resolve();
        }
        return;
    }

    if (message.type === "stopped") {
        updateConsumedSnapshot(graph, message);
        const pending = graph.pendingStops.get(message.runId);
        if (pending) {
            graph.pendingStops.delete(message.runId);
            pending.resolve();
        }
        return;
    }

    if (message.type === "recycle") {
        recycleBuffer(graph, message.buffer);
        return;
    }

    if (message.runId !== graph.runId || graph.runId === 0) {
        return;
    }

    if (message.type === "need") {
        graph.pendingDemand = message.frames;
        if (graph.resolveDemand) {
            const resolve = graph.resolveDemand;
            graph.resolveDemand = null;
            graph.rejectDemand = null;
            const frames = graph.pendingDemand;
            graph.pendingDemand = null;
            resolve(frames);
        }
    } else if (message.type === "first-frame") {
        const observedResumeToFirstFrameLatency = graph.resumeRunId === message.runId &&
            Number.isFinite(graph.resumePerformanceTime)
            ? Math.max(0, observedPerformanceTime - graph.resumePerformanceTime)
            : 0;
        const startToOutputLatencySeconds = Math.max(
            0,
            (estimateOutputPerformanceTime(graph, message.contextTime) -
                graph.runStartPerformanceTime) / 1000);
        graph.metrics.hasFirstFrame = true;
        graph.metrics.firstFrameContextTime = message.contextTime;
        graph.metrics.hasStartToOutputLatency = true;
        graph.metrics.startToOutputLatencySeconds = startToOutputLatencySeconds;
        enqueueEvent(graph, {
            type: "first-frame",
            contextTime: message.contextTime,
            startToOutputLatency: startToOutputLatencySeconds,
            observedResumeToFirstFrameLatency,
        });
    } else if (message.type === "underrun") {
        graph.metrics.underrunCount++;
        graph.metrics.underrunFrames += message.frames;
        enqueueEvent(graph, {
            type: "underrun",
            frames: message.frames,
        });
    } else if (message.type === "drained" && graph.drainResolve) {
        const resolve = graph.drainResolve;
        graph.drainResolve = null;
        graph.drainReject = null;
        resolve();
    }
}

export function waitForDemand(handle, runId) {
    const graph = getGraph(handle);
    if (graph.error) {
        return Promise.reject(graph.error);
    }
    if (graph.runId !== runId || graph.disposed) {
        return Promise.resolve(0);
    }
    if (graph.pendingDemand != null) {
        const frames = graph.pendingDemand;
        graph.pendingDemand = null;
        return Promise.resolve(frames);
    }

    return new Promise((resolve, reject) => {
        graph.resolveDemand = resolve;
        graph.rejectDemand = reject;
    });
}

export function waitForEvent(handle, runId) {
    const graph = getGraph(handle);
    if (graph.error) {
        return Promise.reject(graph.error);
    }
    if (graph.runId !== runId || graph.disposed) {
        return Promise.resolve({ type: "stopped" });
    }
    if (graph.pendingEvents.length > 0) {
        return Promise.resolve(graph.pendingEvents.shift());
    }

    return new Promise((resolve, reject) => {
        graph.resolveEvent = resolve;
        graph.rejectEvent = reject;
    });
}

function acquireBuffer(graph, byteLength) {
    let selectedIndex = -1;
    let selectedLength = Number.POSITIVE_INFINITY;
    for (let index = 0; index < graph.recycledBuffers.length; index++) {
        const candidateLength = graph.recycledBuffers[index].byteLength;
        if (candidateLength >= byteLength && candidateLength < selectedLength) {
            selectedIndex = index;
            selectedLength = candidateLength;
        }
    }

    if (selectedIndex < 0) {
        return new ArrayBuffer(byteLength);
    }
    return graph.recycledBuffers.splice(selectedIndex, 1)[0];
}

function copyFromMemoryView(source, destination) {
    if (typeof source.copyTo === "function") {
        source.copyTo(destination);
    } else {
        destination.set(source);
    }
}

export function enqueue(handle, runId, data, frameCount) {
    const graph = getGraph(handle);
    if (graph.error) {
        throw graph.error;
    }
    if (graph.runId !== runId || graph.disposed) {
        return;
    }

    const sampleCount = frameCount * graph.channels;
    const byteLength = sampleCount * Float32Array.BYTES_PER_ELEMENT;
    const buffer = acquireBuffer(graph, byteLength);
    // A .NET Span is projected as MemoryView, whose bytes are exposed through copyTo rather
    // than numeric properties. Copy synchronously before transferring the buffer to the worklet.
    copyFromMemoryView(data, new Uint8Array(buffer, 0, byteLength));
    graph.node.port.postMessage(
        { type: "samples", runId, buffer, sampleCount },
        [buffer]);
}

export function drain(handle, runId) {
    const graph = getGraph(handle);
    if (graph.error) {
        return Promise.reject(graph.error);
    }
    if (graph.runId !== runId) {
        return Promise.resolve();
    }

    return new Promise((resolve, reject) => {
        graph.drainResolve = resolve;
        graph.drainReject = reject;
        graph.node.port.postMessage({ type: "drain", runId });
    });
}

export function pause(handle) {
    return getGraph(handle).context.suspend();
}

export function resume(handle) {
    const graph = getGraph(handle);
    graph.resumeRunId = graph.runId;
    graph.resumePerformanceTime = performance.now();
    return graph.context.resume();
}

export function setVolume(handle, volume) {
    const graph = graphs.get(handle);
    if (graph?.gain) {
        graph.gain.gain.value = volume;
    }
}

export async function stop(handle, runId) {
    const graph = graphs.get(handle);
    if (!graph || graph.runId !== runId) {
        return;
    }

    const stopped = new Promise((resolve, reject) => {
        graph.pendingStops.set(runId, { resolve, reject });
    });
    graph.node.port.postMessage({ type: "stop", runId });
    graph.runId = 0;
    resolveDemandAsStopped(graph);
    resolveEventsAsStopped(graph);
    resolveDrainAsStopped(graph);
    await Promise.all([stopped, graph.context.suspend()]);

    // A new start may have raced the suspend promise; ensure the newest run wins.
    if (graph.runId !== 0 && graph.context.state === "suspended") {
        await graph.context.resume();
    }
}

export function captureTotalConsumedFrameCountLow(handle) {
    const graph = getGraph(handle);
    const current = currentConsumedFrameState(graph);
    const captured = graph.useSharedConsumedFrameState
        ? subtractConsumedFrameState(current, {
            low: graph.consumedBaselineLow,
            high: graph.consumedBaselineHigh,
        })
        : current;
    graph.capturedConsumedLow = captured.low;
    graph.capturedConsumedHigh = captured.high;
    return captured.low | 0;
}

export function getCapturedTotalConsumedFrameCountHigh(handle) {
    return getGraph(handle).capturedConsumedHigh | 0;
}

export function resetTotalConsumed(handle) {
    const graph = getGraph(handle);
    if (graph.error) {
        return Promise.reject(graph.error);
    }
    if (graph.disposed) {
        return Promise.reject(new Error("The browser audio graph has been disposed."));
    }

    if (graph.useSharedConsumedFrameState) {
        const current = currentConsumedFrameState(graph);
        graph.consumedBaselineLow = current.low;
        graph.consumedBaselineHigh = current.high;
        graph.capturedConsumedLow = 0;
        graph.capturedConsumedHigh = 0;
        return Promise.resolve();
    }

    const resetId = ++graph.nextConsumedResetId;
    return new Promise((resolve, reject) => {
        graph.pendingConsumedResets.set(resetId, { resolve, reject });
        graph.node.port.postMessage({ type: "reset-consumed", resetId });
    });
}

export function getMetrics(handle) {
    const graph = getGraph(handle);
    return { ...graph.metrics };
}

export async function disposeGraph(handle) {
    const graph = graphs.get(handle);
    if (!graph) {
        return;
    }

    graphs.delete(handle);
    await closeGraph(graph);
}
