# NAudio.BrowserAudioWorklet

`NAudio.BrowserAudioWorklet` adds browser audio output to NAudio through the Web
Audio `AudioWorklet` API. It is intended for Avalonia Browser applications and
also works in other .NET WebAssembly applications.

The package exposes:

| Type | Role |
| --- | --- |
| `BrowserAudioWorkletPlayer` | Persistent browser `IWavePlayer` for `IWaveProvider` and `ISampleProvider` sources |
| `BrowserAudioWorkletOptions` | Explicit target-buffer, first-block, and device-rate settings |
| `BrowserAudioLatencyProfile` | `Interactive`, `Balanced`, and `Playback` presets |
| `LatencyMeasureHelper` | Six-probe measurement of resume-to-first-frame main-thread notification latency |
| `ISeekableSampleProvider` | Optional source contract used by `SeekAsync` |
| `BrowserAudioLatencyInfo` | Actual context sample rate and browser-reported latency |
| `BrowserAudioWorkletPlayer.BaseLatency` | Read-only `AudioContext.baseLatency` in seconds after preparation |
| `BrowserAudioWorkletPlayer.OutputLatency` | Read-only `AudioContext.outputLatency` in seconds after preparation |
| `BrowserAudioPlaybackMetrics` | First-frame and underrun counters for the current run |
| `BrowserAudioWorkletPlayer.TotalConsumedFrameCount` | Synchronous cumulative output-frame count |
| `BrowserAudioWorkletPlayer.TotalConsumedSampleCount` | Cumulative interleaved sample count |
| `BrowserAudioWorkletPlayer.TotalConsumedTime` | Cumulative duration at the output sample rate |
| `BrowserAudioException` | Web Audio failure reported through `PlaybackStopped` |

## Platform

The real output backend targets `net10.0-browser`. The package also contains a
`net10.0` target so code can reference the type and its state machine can be
tested outside a browser. Calling a public player constructor outside WebAssembly
throws `PlatformNotSupportedException`.

The package carries both JavaScript modules as static web assets. A consuming
WebAssembly application publishes them automatically under:

```text
_content/NAudio.BrowserAudioWorklet/
```

No script tag or manual `AudioWorklet.addModule` call is required.

```powershell
dotnet add package NAudio.BrowserAudioWorklet
```

## Data Flow

```text
IWaveProvider or ISampleProvider
    -> interleaved Float32 (device-rate resampling when needed)
    -> reusable managed render array
    -> one copy into a recycled transferable ArrayBuffer
    -> AudioWorkletProcessor block queue
    -> GainNode
    -> speakers
```

The player writes a small first block directly into the transport before
`AudioContext.resume()`. The processor can therefore render source audio on its
first quantum while the remaining target buffer is filled in the background.
Transferred buffers are consumed in place on the audio thread and returned to a
small main-thread pool instead of being allocated for every request.

## Low-Latency Usage

Initialize once and prepare as early as practical. Call `PlayAsync` directly from
the click or tap handler so `AudioContext.resume()` retains user activation.

```csharp
using NAudio.Wave.Browser;

using var output = new BrowserAudioWorkletPlayer(
    BrowserAudioLatencyProfile.Interactive);

output.Init(sampleProvider); // Direct ISampleProvider path avoids adapter copies.
BrowserAudioLatencyInfo latency = await output.PrepareAsync();

playButton.Click += async (_, _) => await output.PlayAsync();
```

The profiles select these target queue durations:

| Profile | Target | Intended use |
| --- | ---: | --- |
| `Interactive` | 20 ms | Effects, instruments, games, and immediate controls |
| `Balanced` | 80 ms | Responsive media playback with moderate stall tolerance |
| `Playback` | 250 ms | Music playback where uninterrupted output is preferred |

The default constructor uses `Playback`. Startup is still two-stage, so the full
250 ms queue is not filled before the first sound. Use `Interactive` or `Balanced`
when source changes and seeks must become audible sooner.

For explicit control:

```csharp
using var output = new BrowserAudioWorkletPlayer(new BrowserAudioWorkletOptions
{
    BufferDurationMilliseconds = 40,
    InitialBufferFrameCount = 512,
    UseDeviceSampleRate = true,
});
```

The buffer duration range is 20 to 5000 ms and the first block range is 128 to
8192 frames. `UseDeviceSampleRate` defaults to `true`; the browser chooses the
output device's native rate and NAudio resamples the source only when required,
avoiding a second browser output resampler.

## Seek And Lifecycle

Sources that implement `ISeekableSampleProvider` can seek without rebuilding the
audio graph:

```csharp
await output.SeekAsync(TimeSpan.FromSeconds(30));
```

`SeekAsync` changes the source position and flushes queued worklet blocks. The
current playing or paused state is preserved. `FlushAsync` is also available when
the application moves a source by another mechanism.

- `Pause()` suspends the context; `PlayAsync()` resumes the existing run.
- `Stop()` clears the current run and suspends the prepared context.
- A later `PlayAsync()` reuses the same `AudioContext` and `AudioWorkletNode`.
- Natural end of stream drains queued frames, suspends the graph, and raises
  `PlaybackStopped` once.
- `Dispose()` is the operation that closes the persistent `AudioContext`.

## Diagnostics

`PrepareAsync` returns the actual output sample rate, base latency, output
latency, and target frame capacity. The same two browser values are available
directly as the read-only `BaseLatency` and `OutputLatency` properties after
preparation; both are measured in seconds and return zero before preparation.
Runtime diagnostics are available through:

```csharp
output.FirstFrameRendered += (_, e) =>
    Console.WriteLine($"Estimated click-to-output: " +
        $"{e.EstimatedStartToOutputLatencySeconds * 1000:F1} ms");

output.BufferUnderrun += (_, e) =>
    Console.WriteLine($"Missing frames: {e.MissingFrames}");

BrowserAudioPlaybackMetrics metrics = await output.GetPlaybackMetricsAsync();
```

The first-frame estimate includes preparation time spent after `PlayAsync` was
requested and maps the worklet's context timestamp to the output device when the
browser exposes `getOutputTimestamp()`. Underruns count frames emitted as silence
while the WebAssembly main thread could not refill the queue in time.

## First-Frame Latency Measurement

`LatencyMeasureHelper` measures a narrower startup boundary with a temporary real
player. Invoke it after Web Audio has already been authorized and, where possible,
directly from the click or touch handler that requests the measurement:

```csharp
measureButton.Click += async (_, _) =>
{
    TimeSpan latency = await LatencyMeasureHelper.MeasureLatency(
        BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Interactive));

    Console.WriteLine($"Resume-to-first-frame message: {latency.TotalMilliseconds:F1} ms");
};
```

The helper strictly uses the supplied `BrowserAudioWorkletOptions`. It prepares
one `BrowserAudioWorkletPlayer`, `AudioContext`, and `AudioWorkletNode`, then reuses
them for six muted 100 ms probes generated at 48 kHz in stereo: a 440 Hz sine at
0.2 source gain. The temporary player's output volume is fixed at zero, so the
real source frames still exercise the Worklet without producing an audible tone.
The first run warms the graph and is discarded; the returned `TimeSpan` is the
arithmetic mean of the remaining five runs. The source is reset before every run,
and all browser audio resources are closed before the task completes.

Each run starts timing in JavaScript immediately before calling
`AudioContext.resume()`. It stops when the main-thread transport receives the
processor's `first-frame` message. The value therefore includes AudioWorklet-to-
main-thread message delivery, but excludes the Web Audio output chain and physical
device latency. It is not the time at which the speaker actually produces sound.
The muted measurement still requires normal Web Audio authorization; autoplay
and other startup errors propagate through the normal `BrowserAudioException`
path. A run that does not produce both a first-frame notification and a natural
stop within 10 seconds fails with `TimeoutException`.

The source-reference `BrowserAudioWorkletDemo` includes a **Measure latency
(silent)** button that invokes the helper directly and shows the measured value
in its status panel.

## Exact Consumption Progress

The player also exposes a lightweight synchronous polling surface for the
AudioWorklet's actual render progress:

```csharp
long frames = output.TotalConsumedFrameCount;
long samples = output.TotalConsumedSampleCount;
TimeSpan time = output.TotalConsumedTime;

await output.ResetTotalConsumedAsync();
```

`TotalConsumedFrameCount` counts output frames copied from source blocks, not
interleaved scalar samples. `TotalConsumedSampleCount` is the frame count times
the output channel count, and `TotalConsumedTime` divides frames by
`OutputWaveFormat.SampleRate` (including the device-rate/resampling path).
Actual source silence is still copied audio and counts; queued frames and
underrun-generated silence do not. The values stop at the AudioWorklet render
boundary and do not include physical output-device latency.

The counter is zero for a new player and is cleared only by an explicit
`ResetTotalConsumedAsync` call. Play, pause, stop, flush, seek, and natural end
leave it unchanged. In a cross-origin-isolated page, a `SharedArrayBuffer` and
`Atomics` maintain a stable `sequence/low/high` snapshot while the main thread
keeps the reset baseline. Other deployments use exact low/high messages for each
render quantum and an acknowledged reset; synchronous getters return the last
confirmed snapshot, not a clock-based approximation.

Because a sample provider can be read ahead into the queue, applications that
need a user-facing position should retain a start position and calculate:

```text
display position = start position + TotalConsumedTime
```

Reset the counter when starting a new run, after seeking, or when stopping if
the next run should have a fresh position baseline. The music and sine-wave
samples show this pattern and display frames, samples, and `TimeSpan` values.

## Other Behavior

- `Volume` accepts `0.0` to `1.0` and updates a Web Audio `GainNode`, so queued
  samples respond immediately.
- Web Audio supports at most 32 channels; wider sources are rejected.
- Web Audio and interop failures are wrapped in `BrowserAudioException` for
  `PlaybackStopped`. `PlayAsync` also faults for startup and resume failures.
- Compressed formats still need a browser-compatible decoder. The music sample
  uses `decodeAudioData`, caches selected/next tracks, and reuses the graph for
  Stop/replay and Seek.

See `samples/BrowserAudioWorkletDemo` and `samples/BrowserMusicPlayerDemo` for
runnable Avalonia Browser applications.
