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

## 2026-07-31 LatencyMeasureHelper Final Result

The latency helper implementation and its bounded test suite are complete.

### Clean validation

- Release solution build: 5 projects built with 0 warnings and 0 errors.
- NUnit/Microsoft Testing Platform: 90 passed, 0 failed, 0 skipped. The new
  `LatencyMeasureHelperTests` contributes 13 cases covering API shape, averaging, lifecycle,
  source reset, option forwarding, errors, timeouts, argument validation, and non-browser use.
- Complete Node suite: 14 passed, 0 failed. The transport test pins the pre-resume timestamp,
  observed first-frame latency, existing output-estimate semantics, run isolation, and stale-node
  isolation.
- Both shipping AudioWorklet JavaScript files passed `node --check`.
- `eng/Test-Package.ps1` completed successfully after the initial AOT cache warm-up. The consumer
  resolved `NAudio.BrowserAudioWorklet/0.1.0` as a NuGet `package`, built in Release with 0 warnings
  and 0 errors, and completed WebAssembly AOT publish. The package contains both static Worklet
  assets.
- Live Edge acceptance used an untracked temporary copy of the existing demo and a real click
  gesture. The six-run helper completed with a non-negative 6.6 ms result and no application-origin
  console warning or error. The automation environment cannot independently attest physical sound
  output. The temporary browser tab, server on port 5297, and temporary demo directory were closed
  and removed after the check; no tracked demo source was changed.
- Workspace checks: `git diff --check` passed; no microphone or acoustic-loopback API was added.

### Pseudo-mutation and assertion review

- Killed: including the warmup in the average (`4.2 ms` instead of the asserted `4 ms`).
- Killed: resetting the probe only for the first run (later-run sample/reset assertions failed).
- Killed: recording `performance.now()` after `AudioContext.resume()` (reported `7.5 ms` instead of
  the asserted `12.5 ms`).
- Killed: removing stale-node filtering (the old node completed or polluted the replacement run).
- One candidate mutation that removed `naturallyStopped.Task` from the explicit `Task.WhenAll`
  survived because the player's own `Playing` state prevents a following run from advancing when
  the natural-stop callback is absent. The mutation was reverted; production retains the explicit
  first-frame-plus-natural-stop wait, and both missing-event timeout cases pass. This is recorded as
  a behaviorally redundant survivor rather than a perfect mutation score.
- Assertion-quality review found exact equality, approximate signal-value, reflection/API-shape,
  exception identity/type, timeout, negative isolation, collection length, and lifecycle side-effect
  assertions. None of the new tests is assertion-free or trivial-only.

### Commands

```powershell
dotnet build .\NAudio.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release
node --test .\tests\javascript\*.test.mjs
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet.js
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet-processor.js
.\eng\Test-Package.ps1
git diff --check
```

| Requirement | Evidence |
| --- | --- |
| `public static Task<TimeSpan> MeasureLatency(BrowserAudioWorkletOptions options);` | `MeasureLatency_PublicApi_UsesBrowserOptionsAndHasNoAliasOrPublicTimingProperty` |
| `先执行 1 次预热并丢弃结果，再执行 5 次正式测量` | `MeasureLatency_AveragesFiveRunsAfterWarmup_UsesOnePlayerAndResetsProbe` asserts six starts and an exact 4 ms mean from scripted values 1..6 ms. |
| `播放器、AudioContext 和 WorkletNode 在全部测量间复用，结束后确定性释放。` | The same managed test asserts one prepare, six starts, and one dispose; live Edge completed the helper and the temporary session was cleaned. |
| `探测源固定为 48 kHz、双声道、440 Hz、增益 0.2 的 Sin 波，每次持续 100 ms` | The managed test asserts 48 kHz, two channels, 4,800 frames/9,600 samples, gain/frequency sample values, and identical reset starts for all six runs. |
| `每个 run 同时等待首帧和自然停止；10 秒内未收到所需事件时抛出 TimeoutException。` | `MeasureLatency_WhenRequiredEventIsMissing_ThrowsTimeoutAndReleasesPlayer` covers each missing event; `MeasureLatency_PropagatesNaturalStopFailureAndReleasesPlayer` covers stop failure. |
| `起点是在 JavaScript resume(handle) 调用 AudioContext.resume() 前记录的 performance.now()` | `transport measures resume-to-first-frame latency per run and ignores stale nodes` advances the clock inside fake `resume()` and asserts the pre-resume boundary exactly. |
| `现有 EstimatedStartToOutputLatencySeconds 语义保持不变。` | The same transport test separately asserts the existing `startToOutputLatency` event and metrics value (`0.0525`). |
| `不增加额外公开诊断属性。` | Public API reflection asserts no public `ObservedResumeToFirstFrameLatency` property exists while the helper consumes the internal value. |
| `null options 抛出 ArgumentNullException；非法 option 沿用播放器验证；非 Browser/WASM 目标抛出 PlatformNotSupportedException。` | `MeasureLatency_NullOptions_ThrowsArgumentNullExceptionBeforePlatformCheck`, four `MeasureLatency_InvalidOptions_UsesPlayerValidation` cases, and `MeasureLatency_OnNonBrowserTarget_ThrowsPlatformNotSupportedException`. |
| `README 明确说明调用方式、六个可听短音、计时边界、自动播放前提和“不代表扬声器实际出声时间”。` | Root README and package README contain the click/touch example, six-probe description, timing boundary, autoplay prerequisite, and speaker-latency exclusion. |
| `不修改现有 Demo UI，也不引入麦克风权限或声学回环。` | Final `git diff --name-only` contains no tracked demo file; source audit found no microphone or loopback additions. |

## 2026-07-31 AudioContext Latency Properties

Implemented read-only `BrowserAudioWorkletPlayer.BaseLatency` and `OutputLatency` properties as
direct projections of the existing prepared `BrowserAudioLatencyInfo` values. Both report seconds
and return zero before `PrepareAsync` completes; the JavaScript transport was intentionally left
unchanged because it already supplies the values.

Validation:

- `AudioContextLatencyProperties_AreReadOnlyAndZeroBeforePreparation` passed with zero pre-prepare
  values, non-writable reflection metadata, and the configured post-prepare values.
- `PrepareAsync_IsIdempotentAndPublishesLatencyInfo` passed with the new direct values alongside
  the original latency record assertions.
- Focused Release test run: 26 passed, 0 failed, 0 skipped.

## 2026-07-31 Silent Probe and Demo Button Addendum

This addendum supersedes the earlier audible-probe and no-Demo-change assumptions.

### Result

- `LatencyMeasureHelper` keeps the real 48 kHz stereo 440 Hz / 0.2 source probe but sets the
  temporary player's output volume to exactly `0.0f` before preparation. All six runs therefore
  exercise real non-zero source frames through the AudioWorklet while the GainNode output is muted.
- `BrowserAudioWorkletDemo` now exposes a compiled-binding **Measure latency (silent)** button.
  Its command prevents normal playback from starting concurrently, shows an in-progress status,
  invokes the helper with the Interactive options, displays the result in milliseconds, and reports
  the root failure message.
- The root README and package README now describe muted probes and the Demo button; they no longer
  claim that six audible tones are produced.

### Test-gap and assertion review

- Empirical mutation: changing the helper output volume from `0.0f` to `1.0f` caused
  `MeasureLatency_UsesZeroOutputGainBeforeEveryRunWhileRenderingNonZeroProbeFrames` to fail on both
  the complete volume history and all six per-start volume values. The mutation was reverted and
  the same focused test returned green.
- The mute test also asserts the expected non-zero second sine sample, 4,800 frames / 9,600 samples
  per run, six starts, one prepare, one dispose, and the exact five-run average. This prevents an
  all-zero source from satisfying the mute requirement accidentally.
- Assertion categories used by the new/strengthened test include equality, collection, comparison,
  approximate floating-point, and lifecycle/state side effects. It is neither assertion-free nor
  trivial-only and contains no self-referential assertion.
- Static pairing scan: 112 source files, 11 test files, 25 paired source files, 87 unpaired source
  files, and one orphan test. `LatencyMeasureHelper.cs` pairs to `LatencyMeasureHelperTests.cs`;
  generated `artifacts/` files dominate the unpaired list. This is a static heuristic, not line or
  branch coverage.

### Browser acceptance

- The tracked Demo was built and opened in Edge at `http://127.0.0.1:5287/`.
- The button was visible, clickable, repeatable, and displayed `First-frame latency: 6.6 ms`.
- Application-origin console errors/warnings: none.
- Chrome Web Audio lifecycle events showed one temporary 48 kHz context cycling through the six
  running/suspended runs and finally reaching `contextState: closed`.
- Physical sound cannot be sensed by browser automation; silence is pinned at the managed-to-Web
  Audio boundary by the zero-volume test above. The browser tab and local server were closed.

### Clean validation

- Focused latency tests: 13 passed, 0 failed, 0 skipped.
- Full NUnit/Microsoft Testing Platform suite: 90 passed, 0 failed, 0 skipped.
- Complete Node suite: 14 passed, 0 failed.
- Release solution build: 0 warnings, 0 errors, including the Demo compiled XAML bindings.
- Both shipping Worklet files passed `node --check`.
- `eng/Test-Package.ps1` completed package restore, Release build, and WebAssembly AOT publish with
  0 warnings and 0 errors.

| Requirement | Evidence |
| --- | --- |
| `测试不应该发出音量` | `MeasureLatency_UsesZeroOutputGainBeforeEveryRunWhileRenderingNonZeroProbeFrames` asserts zero gain for every run while retaining non-zero sine PCM; the `1.0f` mutation was empirically killed. |
| `为Demo添加按钮可以测试这个功能` | `MainView.axaml` binds **Measure latency (silent)** to `MeasureLatencyCommand`; Release compiled-binding build passed, and the Edge acceptance click displayed `First-frame latency: 6.6 ms` with no app console error and a final closed AudioContext. |
