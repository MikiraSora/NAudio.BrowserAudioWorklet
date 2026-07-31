# BrowserAudioWorklet Test Status

Date: 2026-07-31

## Result

- NUnit/Microsoft Testing Platform: 69 passed, 0 failed, 0 skipped (Release).
- Node test runner: 3 passed, 0 failed (transport, processor, and music decoder).
- JavaScript syntax checks: the transport, processor, and music decoder modules all parsed.
- Main solution Release build: library (`net10.0`, `net10.0-browser`), tests, and both
  browser demos built with 0 warnings and 0 errors.
- NuGet pack and package-only validation: version 0.1.0 was packed, the consumer restored
  the library as a NuGet `package`, and Release build plus AOT publish completed with 0
  warnings and 0 errors.
- Static web assets: both AudioWorklet files are present in the package and their SHA-256
  hashes match the published `_content/NAudio.BrowserAudioWorklet/` files.
- Live Edge validation: prepare/play/pause/resume/flush/stop/replay completed without app
  console errors. A fresh run reported approximately 8.6 ms estimated start-to-output
  latency; a warm replay reported approximately 27.8 ms under automation. Stop/replay
  reused the same AudioContext and AudioWorkletNode.
- Workspace hygiene: `git diff --check` passed and port 5287 was stopped after validation.

## Commands

```powershell
dotnet build .\NAudio.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release --no-build
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet.js
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet-processor.js
node --check .\samples\BrowserMusicPlayerDemo\wwwroot\music-decoder.js
node --test .\tests\javascript\*.test.mjs
.\eng\Test-Package.ps1
```
