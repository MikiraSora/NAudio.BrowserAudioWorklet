# NAudio.Avalonia.BrowserAudioWorklet

This repository contains the browser AudioWorklet backend for NAudio and two
Avalonia Browser samples. It is intentionally independent from the NAudio source
tree: NAudio is consumed from NuGet, while the package in `src/` supplies the
browser-specific player and its static web assets.

## Projects

- `src/NAudio.Avalonia.BrowserAudioWorklet`: the `BrowserAudioWorkletPlayer`
  implementation and its AudioWorklet JavaScript modules.
- `tests/NAudio.Avalonia.BrowserAudioWorklet.Tests`: platform-neutral NUnit tests
  using an injected bridge.
- `samples/BrowserAudioWorkletDemo`: a source-reference Avalonia Browser sample.
- `samples/BrowserMusicPlayerDemo`: a source-reference music player sample. It picks
  audio files or a whole folder (recursive) into a playlist, decodes mp3/ogg/wav with
  the browser's own `AudioContext.decodeAudioData`, and plays through
  `BrowserAudioWorkletPlayer` with seek/play/pause/stop/volume controls.
- `samples/BrowserAudioWorkletPackageDemo`: a package-reference sample. It has no
  project reference to the library and is built by `eng/Test-Package.ps1` after a
  local package is produced.

The package-reference sample is intentionally outside the main solution because
its package is generated locally as part of the validation flow.

## Build and test

```powershell
dotnet restore .\NAudio.Avalonia.BrowserAudioWorklet.slnx
dotnet build .\NAudio.Avalonia.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.Avalonia.BrowserAudioWorklet.Tests\NAudio.Avalonia.BrowserAudioWorklet.Tests.csproj -c Release
```

To pack the library and validate a clean package consumer:

```powershell
.\eng\Test-Package.ps1
```

The script packs version `0.1.0` to `artifacts/packages`, restores the package-only
sample from that local feed, verifies that the assets file records a NuGet package
dependency, and publishes the sample.

After validation, run that package-only sample at `http://127.0.0.1:5297/`:

```powershell
dotnet run --project .\samples\BrowserAudioWorkletPackageDemo\BrowserAudioWorkletPackageDemo.csproj
```

The music player sample runs at `http://127.0.0.1:5299/`:

```powershell
dotnet run --project .\samples\BrowserMusicPlayerDemo\BrowserMusicPlayerDemo.csproj
```

Folder picking relies on the File System Access API, so it needs a Chromium-based
browser; file picking and playback also work elsewhere.

## Package usage

```powershell
dotnet add package NAudio.Avalonia.BrowserAudioWorklet --version 0.1.0
```

The package targets `net9.0` and `net9.0-browser`. Its JavaScript files are static
web assets, so an Avalonia Browser consumer does not need a manual script tag or a
separate `AudioWorklet.addModule` call. See the package README under
`src/NAudio.Avalonia.BrowserAudioWorklet/README.md` for the player API and data flow.
