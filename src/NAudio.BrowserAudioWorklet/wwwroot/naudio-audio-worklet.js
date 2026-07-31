// Main-thread Web Audio transport for NAudio.BrowserAudioWorklet.
// Each player owns one persistent AudioContext and AudioWorkletNode. Playback runs only reset the
// processor queue, which keeps stop/replay and seek off the graph-construction path.

const graphs = new Map();
const MAX_RECYCLED_BUFFERS = 4;

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
    const node = new AudioWorkletNode(graph.context, "naudio-block-queue-processor", {
        numberOfInputs: 0,
        numberOfOutputs: 1,
        outputChannelCount: [graph.channels],
        processorOptions: { channels: graph.channels },
    });
    node.port.onmessage = (event) => onProcessorMessage(graph, event.data);
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
        pendingDemand: null,
        resolveDemand: null,
        rejectDemand: null,
        pendingEvents: [],
        resolveEvent: null,
        rejectEvent: null,
        drainResolve: null,
        drainReject: null,
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

function onProcessorMessage(graph, message) {
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
    // The imported MemoryView is an exact-length Span projection. It is array-like but does not
    // expose every TypedArray helper on every .NET browser runtime, so copy it directly.
    new Uint8Array(buffer, 0, byteLength).set(data);
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
    return getGraph(handle).context.resume();
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

    graph.node.port.postMessage({ type: "stop", runId });
    graph.runId = 0;
    resolveDemandAsStopped(graph);
    resolveEventsAsStopped(graph);
    resolveDrainAsStopped(graph);
    await graph.context.suspend();

    // A new start may have raced the suspend promise; ensure the newest run wins.
    if (graph.runId !== 0 && graph.context.state === "suspended") {
        await graph.context.resume();
    }
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
