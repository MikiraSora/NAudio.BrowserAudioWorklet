// Audio-thread processor for NAudio.Avalonia.BrowserAudioWorklet.

const RENDER_QUANTUM = 128;

class NAudioRingBufferProcessor extends AudioWorkletProcessor {
    constructor(options) {
        super();

        this.channels = options.processorOptions.channels;
        this.capacityFrames = Math.max(
            RENDER_QUANTUM * 4,
            Math.ceil(options.processorOptions.bufferFrameCount));
        this.buffer = new Float32Array(this.capacityFrames * this.channels);
        this.readIndex = 0;
        this.writeIndex = 0;
        this.storedSamples = 0;
        this.lowWaterFrames = Math.floor(this.capacityFrames / 2);
        this.needOutstanding = false;
        this.draining = false;
        this.stopped = false;

        this.port.onmessage = (event) => this.onMessage(event.data);
        this.requestMoreIfNeeded();
    }

    onMessage(message) {
        if (message.type === "samples") {
            this.write(new Float32Array(message.buffer));
            this.needOutstanding = false;
        } else if (message.type === "drain") {
            this.draining = true;
        } else if (message.type === "stop") {
            this.stopped = true;
            this.storedSamples = 0;
        }
    }

    write(samples) {
        if (samples.length % this.channels !== 0) {
            throw new Error("NAudio AudioWorklet received a partial audio frame.");
        }
        if (samples.length > this.buffer.length - this.storedSamples) {
            this.grow(this.storedSamples + samples.length);
        }

        const firstLength = Math.min(samples.length, this.buffer.length - this.writeIndex);
        this.buffer.set(samples.subarray(0, firstLength), this.writeIndex);
        if (firstLength < samples.length) {
            this.buffer.set(samples.subarray(firstLength), 0);
        }
        this.writeIndex = (this.writeIndex + samples.length) % this.buffer.length;
        this.storedSamples += samples.length;
    }

    grow(minSamples) {
        let newCapacity = this.buffer.length * 2;
        while (newCapacity < minSamples) {
            newCapacity *= 2;
        }

        const grown = new Float32Array(newCapacity);
        const firstLength = Math.min(this.storedSamples, this.buffer.length - this.readIndex);
        grown.set(this.buffer.subarray(this.readIndex, this.readIndex + firstLength), 0);
        if (firstLength < this.storedSamples) {
            grown.set(this.buffer.subarray(0, this.storedSamples - firstLength), firstLength);
        }

        this.buffer = grown;
        this.readIndex = 0;
        this.writeIndex = this.storedSamples;
        this.capacityFrames = newCapacity / this.channels;
        this.lowWaterFrames = Math.floor(this.capacityFrames / 2);
    }

    process(_inputs, outputs) {
        if (this.stopped) {
            return false;
        }

        const output = outputs[0];
        const frameCount = output[0].length;

        for (let frame = 0; frame < frameCount; frame++) {
            if (this.storedSamples >= this.channels) {
                for (let channel = 0; channel < this.channels; channel++) {
                    output[channel][frame] = this.buffer[this.readIndex];
                    this.readIndex = (this.readIndex + 1) % this.buffer.length;
                }
                this.storedSamples -= this.channels;
            } else {
                for (let channel = 0; channel < this.channels; channel++) {
                    output[channel][frame] = 0;
                }
            }
        }

        this.requestMoreIfNeeded();
        if (this.draining && this.storedSamples < this.channels) {
            this.port.postMessage({ type: "drained" });
            return false;
        }

        return true;
    }

    requestMoreIfNeeded() {
        if (this.needOutstanding || this.draining || this.stopped) {
            return;
        }

        const storedFrames = this.storedSamples / this.channels;
        if (storedFrames < this.lowWaterFrames) {
            this.needOutstanding = true;
            this.port.postMessage({
                type: "need",
                frames: this.capacityFrames - storedFrames,
            });
        }
    }
}

registerProcessor("naudio-ring-buffer-processor", NAudioRingBufferProcessor);
