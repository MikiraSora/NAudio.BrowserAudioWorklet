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

## 2026-07-31 Exact Consumption Progress — TDD Baseline (superseded; corrected result below)

The test-only phase added four NUnit cases, three processor cases, and two transport cases. The
requested production API and shared-state implementation do not exist yet, so the first narrow
run is intentionally red rather than reported as completion.

- `dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release`
  stops at compile time with `CS1061` because `BrowserAudioWorkletPlayer.ConsumedFrameCount` has
  not been added yet.
- `node --test .\tests\javascript\audio-worklet-processor.test.mjs .\tests\javascript\audio-worklet-transport.test.mjs`
  reports 2 existing tests passed and 5 new feature tests failed at the missing SharedArrayBuffer,
  seqlock writer, reader, and unsupported-browser contracts.
- `node --check` passes for both edited JavaScript test files, and `git diff --check` is clean.

Pseudo-mutation review of the new assertions finds coverage for the substantive defects: counting
interleaved samples instead of frames, adding silent underrun slots, failing to reset a generation,
clearing progress on stop/drain, truncating above 32 bits, exposing a stale run, returning an
approximation when SharedArrayBuffer is unavailable, and rebuilding the graph during flush. The
assertion set includes exact equality, exception/message, state, negative/stale-run, deep buffer,
and side-effect assertions; none of the new tests is assertion-free or null-only.

## 2026-07-31 AudioWorklet Total Consumption Progress — Corrected Result

The final focused test set contains 8 NUnit tests, 6 processor Node tests, and 5 transport Node
tests for the cumulative API. The old per-run `ConsumedFrameCount` fixture was adapted rather than
discarded; its fake bridge now preserves totals until explicit reset.

Clean runs:

- `dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release`
  — 77 passed, 0 failed, 0 skipped.
- `node --test .\tests\javascript\audio-worklet-processor.test.mjs .\tests\javascript\audio-worklet-transport.test.mjs`
  — 11 passed, 0 failed, 0 skipped.
- `node --check .\tests\javascript\audio-worklet-processor.test.mjs` and
  `node --check .\tests\javascript\audio-worklet-transport.test.mjs` — passed.
- `git diff --check` — passed.

Equivalent test-gap review checked mutations for frame/sample arithmetic, copied-zero versus
underrun silence, lifecycle clearing, low-word carry, signed-64 saturation, odd seqlock reads,
fallback interpolation, reset correlation, stopped final messages, graph failure/disposal, and
stale node messages. Every candidate is pinned by an exact, state, negative, deep, or exception
assertion. No assertion-free or trivial-only test was found. Production mutation injection was
intentionally skipped because this subtask may not edit `src/**`; the review is static, not an
empirical mutation score.

Assertion-quality review found meaningful equality, boolean, comparison, exception, negative,
state/side-effect, and deep assertions in the new NUnit and `node:test` cases. There are no sleeps,
skips, `.only` tests, tautological self-comparisons, or unawaited async assertions. Existing
`BrowserAudioPlaybackMetrics` tests remain unchanged and retain their original per-run semantics.

### End-to-End Final Validation

- Release solution build: 5 projects built with 0 warnings and 0 errors.
- NUnit/Microsoft Testing Platform: 77 passed, 0 failed, 0 skipped.
- Complete Node suite: 13 passed, 0 failed, including the existing decoder regressions.
- Both shipping AudioWorklet JavaScript files passed `node --check`.
- `eng/Test-Package.ps1` packed version 0.1.0, restored the package-only demo from the local
  NuGet package, and completed its Release build plus AOT publish with 0 warnings and 0 errors.
- The package-only demo still contains no references to the cumulative-consumption API.
