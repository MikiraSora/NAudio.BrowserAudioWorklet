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

## Deployment

`.github/workflows/deploy-pages.yml` publishes the music player demo to
GitHub Pages on every push to `main` that touches the library or the demo
(also runnable manually from the Actions tab). Enable it under repo
**Settings → Pages → Source: GitHub Actions**.
