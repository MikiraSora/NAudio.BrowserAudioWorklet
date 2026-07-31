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

## 2026-07-31 MP3 EncodingError Regression

- Root cause: .NET 10 projects `Span<byte>` as a `MemoryView` with `copyTo`; treating it as an
  indexed source for `Uint8Array.set` replaced the compressed MP3 and PCM bytes with zeroes.
- Fix: both the music decoder and AudioWorklet transport now prefer `source.copyTo(destination)`
  while retaining the TypedArray/indexed-array fallback.
- JavaScript regression suite: 4 passed, 0 failed. The generated tests assert exact byte content,
  one `copyTo` call, fallback copying with different recycled-buffer contents, PCM output, state,
  and error behavior.
- Pseudo-mutation review: 4 of 4 injected copy-path mutations were caught; each mutation was
  reverted immediately and the final suite returned to green. No survivor remains in this scope.
- Assertion review: 3 relevant tests have meaningful equality/deep, exception, state, and
  side-effect assertions; none is assertion-free or trivial-only.
- Live Edge regression: `F:\12312313\Fantasy Kaleidoscope ~The Memories of Phantasm~.mp3`
  (1,510,922 bytes, header `FF FB 90 04`) decoded through the final module at 44,100 Hz,
  2 channels, and 4,164,480 frames with exactly one `MemoryView.copyTo` call.
- Final verification: Release solution build completed with 0 warnings and 0 errors; 69 NUnit/MTP
  tests and 4 Node tests passed; JavaScript syntax checks passed; NuGet consumer build and AOT
  publish passed.
- Workspace hygiene: diagnostic MP3/HTML/profile files were removed, ports 5299/5300/9333 were
  stopped, and `git diff --check` passed before final handoff.

| Requirement | Evidence |
| --- | --- |
| `MusicPlayerDemo无法加载mp3音乐文件，抛出EncodingError` | `decoder copies compressed bytes through MemoryView.copyTo before decoding`; live Edge decode of the user's MP3 |
| Preserve AudioWorklet PCM bytes | `transport copies MemoryView bytes before transferring sample blocks` |
| Preserve compatibility fallbacks | `decoder retains indexed array-like fallbacks` and the transport's distinct fallback block assertion |
