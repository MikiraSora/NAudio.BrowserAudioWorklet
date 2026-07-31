# NAudio.BrowserAudioWorklet

Play audio in the browser with NAudio. This repo provides
`BrowserAudioWorkletPlayer`, an NAudio player that runs on WebAssembly
(Avalonia Browser) and outputs sound through the Web Audio **AudioWorklet**
API — no Blazor, no plugins.

**Live demo:** <https://mikirasora.github.io/NAudio.BrowserAudioWorklet/>

## What it does

- Implements an NAudio `IWavePlayer`-style player backed by a real
  AudioWorklet node, so buffered PCM playback, pause/resume, volume and
  position all work from C#.
- Prepares and reuses one browser audio graph, primes the first block before
  resume, and offers 20/80/250 ms latency profiles.
- Uses the output device's native sample rate, recycled transferable blocks,
  direct `ISampleProvider` rendering, graph-preserving Seek/Flush, and
  first-frame/underrun telemetry.
- Exposes exact AudioWorklet consumption progress in output frames, interleaved
  samples, and output-rate `TimeSpan` values, with an explicit asynchronous reset.
- Measures AudioWorklet first-frame notification latency with one muted warmup
  probe and five muted measured probes on a temporary, reusable browser audio graph.
- Ships its JavaScript as static web assets: referencing the project (or the
  NuGet package) is enough — no manual `<script>` tag, no
  `AudioWorklet.addModule` call.
- Decodes compressed audio (mp3/ogg/wav) using the browser's built-in
  `decodeAudioData`, with selected/next-track prefetch in the music player demo.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/NAudio.BrowserAudioWorklet` | The library: `BrowserAudioWorkletPlayer` plus its AudioWorklet JS modules under `wwwroot/`. |
| `tests/NAudio.BrowserAudioWorklet.Tests` | Platform-neutral NUnit tests driven by a fake bridge (no browser needed). |
| `samples/BrowserMusicPlayerDemo` | Music player: pick files or a folder, build a playlist, seek/play/pause/stop/volume. |
| `samples/BrowserAudioWorkletDemo` | Minimal player sample (source reference). |
| `samples/BrowserAudioWorkletPackageDemo` | Same sample but consuming the NuGet package; built by `eng/Test-Package.ps1`. |
| `eng/Test-Package.ps1` | Packs the library locally and validates the package-only sample. |

## Getting started

Requirements: .NET 10 SDK and a Chromium-based browser (folder picking uses the
File System Access API; plain file picking works in other browsers too).

Run the music player demo:

```powershell
dotnet run --project .\samples\BrowserMusicPlayerDemo\BrowserMusicPlayerDemo.csproj
# then open http://127.0.0.1:5299/
```

Build and test everything:

```powershell
dotnet build .\NAudio.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release
node --test .\tests\javascript\*.test.mjs
```

Validate the NuGet package end-to-end (pack → restore → publish the
package-only sample):

```powershell
.\eng\Test-Package.ps1
```

## Use it in your own app

```powershell
dotnet add package NAudio.BrowserAudioWorklet
```

The package targets `net10.0` and `net10.0-browser`. See
`src/NAudio.BrowserAudioWorklet/README.md` for the player API and data flow.

## AudioWorklet consumption progress

`BrowserAudioWorkletPlayer` reports the amount of source audio that the
AudioWorklet has actually copied into output render quanta:

```csharp
long frames = output.TotalConsumedFrameCount;
long samples = output.TotalConsumedSampleCount;
TimeSpan consumed = output.TotalConsumedTime;
await output.ResetTotalConsumedAsync();
```

Frames are output frames and do not multiply by the channel count. Samples are
`frames * output channels`; time uses `OutputWaveFormat.SampleRate`, so it
remains correct when `UseDeviceSampleRate` enables resampling. Copied zero-valued
source samples count, while queued data and silence inserted for an underrun do
not. The values describe AudioWorklet render progress and exclude Web Audio's
physical-device output latency.

Only `ResetTotalConsumedAsync` clears the counter. Play, pause, stop, flush,
seek, and natural end preserve it. When cross-origin isolation enables
`SharedArrayBuffer`, the processor publishes a three-word
`sequence/low/high` atomic snapshot and the main thread applies a reset
baseline. Without that capability, the processor sends exact low/high snapshots
and reset acknowledgements; synchronous getters return the last confirmed
snapshot and never interpolate from a clock.

For a source position, keep an application-owned start position and calculate
`start position + output.TotalConsumedTime`. Do not use the source/provider
position directly: the player may have read ahead into the AudioWorklet queue.

## Measure first-frame notification latency

Call `LatencyMeasureHelper.MeasureLatency` after Web Audio playback has already
been authorized, preferably directly from a click or touch handler. It creates
one temporary `BrowserAudioWorkletPlayer` with the exact options supplied, reuses
its `AudioContext` and `AudioWorkletNode`, and then closes them before completing.

```csharp
measureButton.Click += async (_, _) =>
{
    TimeSpan latency = await LatencyMeasureHelper.MeasureLatency(
        BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Interactive));

    Console.WriteLine($"AudioWorklet first-frame notification: {latency.TotalMilliseconds:F1} ms");
};
```

The call renders six muted 100 ms, 440 Hz sine-wave probes: one warmup whose
result is discarded, then five runs whose arithmetic mean is returned. The
source remains a real non-zero sine wave, while the temporary player's output
volume is fixed at zero so the measurement should not produce an audible tone.
Timing starts immediately before JavaScript calls `AudioContext.resume()` and
ends when the main-thread transport receives the processor's `first-frame`
message, so it includes Worklet-to-main-thread message delivery. It does not
measure the Web Audio output chain or physical device latency and therefore does
not represent the time at which a speaker actually produces sound. The muted
measurement still requires normal Web Audio authorization; autoplay or Web
Audio failures propagate through the same browser exception path as normal
player startup.

`samples/BrowserAudioWorkletDemo` includes a **Measure latency (silent)** button
that invokes the helper from the click handler and displays the returned value.

## Deployment

`.github/workflows/deploy-pages.yml` publishes the music player demo to
GitHub Pages on every push to `main` that touches the library or the demo
(also runnable manually from the Actions tab). Enable it under repo
**Settings → Pages → Source: GitHub Actions**.
