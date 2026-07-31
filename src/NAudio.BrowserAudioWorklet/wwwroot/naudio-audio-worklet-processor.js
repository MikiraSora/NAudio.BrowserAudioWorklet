// Audio-thread block queue for NAudio.BrowserAudioWorklet.
// Transferred buffers are consumed in place and returned to the main thread for reuse.

const DEFAULT_RENDER_QUANTUM = 128;

class NAudioBlockQueueProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();

        const processorOptions = options.processorOptions;
        this.channels = processorOptions.channels;
        this.nodeId = processorOptions.nodeId ?? 0;
        this.consumedFrameState = processorOptions.consumedFrameState
            ? new Int32Array(processorOptions.consumedFrameState)
            : null;
        this.consumedLow = processorOptions.initialConsumedLow >>> 0;
        this.consumedHigh = processorOptions.initialConsumedHigh >>> 0;
        this.chunks = [];
        this.chunkHead = 0;
        this.queuedSamples = 0;
        this.capacityFrames = DEFAULT_RENDER_QUANTUM * 4;
        this.lowWaterFrames = this.capacityFrames / 2;
        this.needOutstanding = false;
        this.initialFillPending = false;
        this.active = false;
        this.draining = false;
        this.disposed = false;
        this.runId = 0;
        this.firstFrameRendered = false;
        this.inUnderrun = false;
        this.currentUnderrunFrames = 0;

        this.port.onmessage = (event) => this.onMessage(event.data);
    }

    onMessage(message) {
        if (message.type === "start" || message.type === "flush") {
            this.beginRun(message);
        } else if (message.type === "samples") {
            this.acceptSamples(message);
        } else if (message.type === "drain" && message.runId === this.runId) {
            this.draining = true;
            this.needOutstanding = false;
        } else if (message.type === "stop" && message.runId === this.runId) {
            this.stopRun(true);
        } else if (message.type === "reset-consumed") {
            this.resetConsumed(message.resetId);
        } else if (message.type === "dispose") {
            this.stopRun();
            this.disposed = true;
        }
    }

    beginRun(message) {
        this.recycleAllChunks();
        this.runId = message.runId;
        this.capacityFrames = Math.max(
            DEFAULT_RENDER_QUANTUM,
            Math.ceil(message.bufferFrameCount));
        this.lowWaterFrames = Math.floor(this.capacityFrames / 2);
        this.needOutstanding = false;
        this.initialFillPending = true;
        this.active = true;
        this.draining = false;
        this.firstFrameRendered = false;
        this.inUnderrun = false;
        this.currentUnderrunFrames = 0;

        if (message.requestInitialBuffer !== false) {
            const initialFrames = Math.min(
                this.capacityFrames,
                Math.max(DEFAULT_RENDER_QUANTUM, Math.ceil(message.initialBufferFrameCount)));
            this.requestFrames(initialFrames);
        }
    }

    acceptSamples(message) {
        if (message.runId !== this.runId || !this.active || this.draining) {
            this.recycleBuffer(message.buffer);
            return;
        }
        if (message.sampleCount % this.channels !== 0) {
            throw new Error("NAudio AudioWorklet received a partial audio frame.");
        }

        this.chunks.push({
            samples: new Float32Array(message.buffer, 0, message.sampleCount),
            offset: 0,
        });
        this.queuedSamples += message.sampleCount;
        this.needOutstanding = false;

        // The first small block can play immediately while this second request fills the rest.
        if (this.initialFillPending) {
            this.initialFillPending = false;
            const queuedFrames = this.queuedSamples / this.channels;
            if (queuedFrames < this.capacityFrames) {
                this.requestFrames(this.capacityFrames - queuedFrames);
            }
        } else {
            this.requestMoreIfNeeded();
        }
    }

    stopRun(reportStopped = false) {
        const stoppedRunId = this.runId;
        this.recycleAllChunks();
        this.active = false;
        this.draining = false;
        this.needOutstanding = false;
        this.initialFillPending = false;
        this.inUnderrun = false;
        this.currentUnderrunFrames = 0;
        if (reportStopped) {
            this.publishConsumedSnapshot();
            this.port.postMessage({
                type: "stopped",
                nodeId: this.nodeId,
                runId: stoppedRunId,
                low: this.consumedLow | 0,
                high: this.consumedHigh | 0,
            });
        }
        this.runId = 0;
    }

    resetConsumed(resetId) {
        this.consumedLow = 0;
        this.consumedHigh = 0;
        this.publishConsumedSnapshot();
        this.port.postMessage({
            type: "consumed-reset",
            nodeId: this.nodeId,
            resetId,
            low: 0,
            high: 0,
        });
    }

    addConsumedFrames(frameCount) {
        if (frameCount <= 0 ||
            (this.consumedHigh === 0x7fffffff && this.consumedLow === 0xffffffff)) {
            return;
        }

        const sum = this.consumedLow + frameCount;
        const nextLow = sum >>> 0;
        const carry = sum > 0xffffffff ? 1 : 0;
        const nextHigh = this.consumedHigh + carry;
        if (nextHigh > 0x7fffffff) {
            this.consumedLow = 0xffffffff;
            this.consumedHigh = 0x7fffffff;
            return;
        }

        this.consumedLow = nextLow;
        this.consumedHigh = nextHigh >>> 0;
    }

    publishConsumedSnapshot() {
        if (this.consumedFrameState) {
            let sequence = Atomics.load(this.consumedFrameState, 0);
            if ((sequence & 1) !== 0) {
                sequence++;
            }
            const writingSequence = (sequence + 1) | 1;
            Atomics.store(this.consumedFrameState, 0, writingSequence);
            Atomics.store(this.consumedFrameState, 1, this.consumedLow | 0);
            Atomics.store(this.consumedFrameState, 2, this.consumedHigh | 0);
            Atomics.store(this.consumedFrameState, 0, writingSequence + 1);
            return;
        }

        this.port.postMessage({
            type: "consumed-snapshot",
            nodeId: this.nodeId,
            runId: this.runId,
            low: this.consumedLow | 0,
            high: this.consumedHigh | 0,
        });
    }

    recycleBuffer(buffer) {
        this.port.postMessage({ type: "recycle", buffer }, [buffer]);
    }

    recycleAllChunks() {
        for (let index = this.chunkHead; index < this.chunks.length; index++) {
            const chunk = this.chunks[index];
            if (chunk?.samples?.buffer?.byteLength > 0) {
                this.recycleBuffer(chunk.samples.buffer);
            }
        }
        this.chunks.length = 0;
        this.chunkHead = 0;
        this.queuedSamples = 0;
    }

    recycleConsumedChunk(chunk) {
        this.recycleBuffer(chunk.samples.buffer);
        this.chunkHead++;
        if (this.chunkHead >= this.chunks.length) {
            this.chunks.length = 0;
            this.chunkHead = 0;
        } else if (this.chunkHead >= 8 && this.chunkHead * 2 >= this.chunks.length) {
            this.chunks = this.chunks.slice(this.chunkHead);
            this.chunkHead = 0;
        }
    }

    process(_inputs, outputs) {
        if (this.disposed) {
            return false;
        }

        const output = outputs[0];
        const frameCount = output[0].length;
        if (!this.active) {
            return true;
        }

        let outputFrame = 0;
        while (outputFrame < frameCount && this.queuedSamples >= this.channels) {
            const chunk = this.chunks[this.chunkHead];
            const availableFrames = (chunk.samples.length - chunk.offset) / this.channels;
            const framesToCopy = Math.min(frameCount - outputFrame, availableFrames);

            for (let channel = 0; channel < this.channels; channel++) {
                let inputIndex = chunk.offset + channel;
                const outputChannel = output[channel];
                for (let frame = 0; frame < framesToCopy; frame++) {
                    outputChannel[outputFrame + frame] = chunk.samples[inputIndex];
                    inputIndex += this.channels;
                }
            }

            const copiedSamples = framesToCopy * this.channels;
            chunk.offset += copiedSamples;
            this.queuedSamples -= copiedSamples;
            outputFrame += framesToCopy;

            if (chunk.offset === chunk.samples.length) {
                this.recycleConsumedChunk(chunk);
            }
        }

        this.addConsumedFrames(outputFrame);
        // Publish once per active render quantum. A zero-copy underrun therefore confirms the
        // same exact value instead of counting the silence already present in the output arrays.
        this.publishConsumedSnapshot();

        if (outputFrame > 0 && !this.firstFrameRendered) {
            this.firstFrameRendered = true;
            this.port.postMessage({
                type: "first-frame",
                runId: this.runId,
                contextTime: currentTime,
            });
        }

        if (!this.draining && outputFrame < frameCount) {
            if (!this.inUnderrun) {
                this.inUnderrun = true;
                this.currentUnderrunFrames = 0;
            }
            this.currentUnderrunFrames += frameCount - outputFrame;
        } else if (this.inUnderrun) {
            this.port.postMessage({
                type: "underrun",
                runId: this.runId,
                frames: this.currentUnderrunFrames,
            });
            this.inUnderrun = false;
            this.currentUnderrunFrames = 0;
        }

        this.requestMoreIfNeeded();
        if (this.draining && this.queuedSamples < this.channels) {
            this.port.postMessage({ type: "drained", runId: this.runId });
            this.active = false;
            this.draining = false;
        }

        // Output arrays are zero-initialized by Web Audio, so unfilled frames already contain silence.
        return true;
    }

    requestFrames(frames) {
        if (this.needOutstanding || this.draining || !this.active) {
            return;
        }

        this.needOutstanding = true;
        this.port.postMessage({
            type: "need",
            runId: this.runId,
            frames,
        });
    }

    requestMoreIfNeeded() {
        if (this.needOutstanding || this.draining || !this.active) {
            return;
        }

        const queuedFrames = this.queuedSamples / this.channels;
        if (queuedFrames < this.lowWaterFrames) {
            this.requestFrames(this.capacityFrames - queuedFrames);
        }
    }
}

registerProcessor("naudio-block-queue-processor", NAudioBlockQueueProcessor);
