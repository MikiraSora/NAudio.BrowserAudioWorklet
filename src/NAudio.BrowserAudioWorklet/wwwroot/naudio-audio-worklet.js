// Main-thread Web Audio transport for NAudio.BrowserAudioWorklet.
// Managed code supplies interleaved Float32 frames when the processor asks for data.

const graphs = new Map();

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

function closeGraph(graph) {
    graph.closing = true;

    if (graph.resolveDemand) {
        graph.resolveDemand(0);
        graph.resolveDemand = null;
        graph.rejectDemand = null;
    }
    if (graph.drainResolve) {
        graph.drainResolve();
        graph.drainResolve = null;
        graph.drainReject = null;
    }

    if (graph.node) {
        graph.node.port.postMessage({ type: "stop" });
        graph.node.disconnect();
    }
    if (graph.gain) {
        graph.gain.disconnect();
    }

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
    if (graph.drainReject) {
        graph.drainReject(graph.error);
        graph.drainResolve = null;
        graph.drainReject = null;
    }
}

export async function start(handle, sampleRate, channels, bufferFrameCount) {
    if (typeof globalThis.AudioContext !== "function" ||
        typeof globalThis.AudioWorkletNode !== "function") {
        throw new Error("This browser does not support the Web Audio AudioWorklet API.");
    }

    const context = new AudioContext({ sampleRate, latencyHint: "interactive" });
    const graph = {
        context,
        node: null,
        gain: null,
        channels,
        pendingDemand: null,
        resolveDemand: null,
        rejectDemand: null,
        drainResolve: null,
        drainReject: null,
        closing: false,
        error: null,
    };
    graphs.set(handle, graph);

    // resume() is invoked before the first await so the browser sees it in the original click or
    // tap activation. Loading the processor can then continue asynchronously.
    const resumePromise = context.resume();

    try {
        await context.audioWorklet.addModule(processorUrl());
        if (graph.closing) {
            await resumePromise.catch(() => undefined);
            return;
        }

        const node = new AudioWorkletNode(context, "naudio-ring-buffer-processor", {
            numberOfInputs: 0,
            numberOfOutputs: 1,
            outputChannelCount: [channels],
            processorOptions: { channels, bufferFrameCount },
        });
        const gain = new GainNode(context, { gain: 1.0 });
        graph.node = node;
        graph.gain = gain;

        node.port.onmessage = (event) => onProcessorMessage(graph, event.data);
        node.onprocessorerror = () => failGraph(
            graph,
            new Error("The AudioWorklet processor stopped because of an audio-thread error."));
        node.connect(gain).connect(context.destination);

        // Do not delay managed prefill on this promise. Any resume failure rejects the next
        // demand wait and is reported through PlaybackStopped.
        resumePromise.catch((error) => failGraph(graph, error));
    } catch (error) {
        const canceled = graph.closing;
        graphs.delete(handle);
        await closeGraph(graph);
        if (!canceled) {
            throw error;
        }
    }
}

function onProcessorMessage(graph, message) {
    if (message.type === "need") {
        graph.pendingDemand = message.frames;
        if (graph.resolveDemand) {
            const resolve = graph.resolveDemand;
            graph.resolveDemand = null;
            graph.rejectDemand = null;
            const frames = graph.pendingDemand;
            graph.pendingDemand = null;
            resolve(graph.closing ? 0 : frames);
        }
    } else if (message.type === "drained" && graph.drainResolve) {
        const resolve = graph.drainResolve;
        graph.drainResolve = null;
        graph.drainReject = null;
        resolve();
    }
}

export function waitForDemand(handle) {
    const graph = getGraph(handle);
    if (graph.error) {
        return Promise.reject(graph.error);
    }
    if (graph.closing) {
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

export function enqueue(handle, data, frameCount) {
    const graph = getGraph(handle);
    if (graph.error) {
        throw graph.error;
    }
    if (graph.closing) {
        return;
    }

    // The MemoryView is valid only for this interop call. Copy exactly the rendered bytes into a
    // standalone buffer, then transfer ownership of that buffer to the audio thread.
    const byteLength = frameCount * graph.channels * Float32Array.BYTES_PER_ELEMENT;
    const bytes = data.slice(0, byteLength);
    graph.node.port.postMessage(
        { type: "samples", buffer: bytes.buffer },
        [bytes.buffer]);
}

export function drain(handle) {
    const graph = getGraph(handle);
    if (graph.error) {
        return Promise.reject(graph.error);
    }
    graph.closing = true;
    return new Promise((resolve, reject) => {
        graph.drainResolve = resolve;
        graph.drainReject = reject;
        graph.node.port.postMessage({ type: "drain" });
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

export async function stop(handle) {
    const graph = graphs.get(handle);
    if (!graph) {
        return;
    }

    graphs.delete(handle);
    await closeGraph(graph);
}
