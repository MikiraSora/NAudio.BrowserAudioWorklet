# BrowserAudioWorklet Independent Repository Research

## Scope

- Production project: `src/NAudio.BrowserAudioWorklet/`.
- Unit-test project: `tests/NAudio.BrowserAudioWorklet.Tests/`.
- Source-reference Demo: `samples/BrowserAudioWorkletDemo/`.
- Package-only Demo: `samples/BrowserAudioWorkletPackageDemo/`.
- Package version under validation: `0.1.0`.

## Repository Conventions

- C# and .NET 10 targets, built by the installed .NET 10 SDK.
- `NAudio.Core` 2.3.0 is consumed from NuGet in the library, tests, and both
  Demos.
- Avalonia packages use 11.3.12.
- The test project is an executable NUnit project using Microsoft Testing
  Platform; `global.json` selects that runner for SDK 10.
- The independent package is unsigned; its internal test seam uses a simple
  `InternalsVisibleTo` assembly name.

## Implementation Inventory

| Component | Responsibility | Evidence strategy |
| --- | --- | --- |
| `BrowserAudioWorkletPlayer` | `IWavePlayer` state, input conversion, events, volume validation | NUnit tests with a fake bridge |
| `IAudioWorkletBridge` | Injectable transport boundary | Deterministic fake bridge |
| `AudioWorkletBridge` | Browser-only `JSImport` calls and graph generations | Browser build plus live Chrome validation |
| JavaScript modules | Main-thread transport and audio-thread ring buffer | Module/package checks plus live Chrome validation |
| NuGet static web assets | Ship both JS modules to consumers | Parsed `.nupkg` and published `_content/` output |

## Packaging Findings

- The Razor class library packages `wwwroot` files as static web assets and the
  consuming WebAssembly app publishes them under `_content/NAudio.BrowserAudioWorklet/`.
- The package exposes both `net10.0` and the normalized browser TFM
  `net10.0-browser1.0` in its NuGet layout.
- `Span<byte>` remains the JavaScript memory-view boundary. Stable NAudio.Core
  2.3.0 supplies array-based `ISampleProvider.Read`, so the player reuses a float
  array and copies its bit-identical bytes into that Span.

## 2026-07-31 Low-Latency Optimization Inventory

- Public targets: preparation, latency profiles, direct `ISampleProvider` initialization,
  device-rate output, flush/seek, first-frame events, underrun events, and metrics.
- Bridge targets: one preparation per player, persistent context semantics, a small initial
  transfer, run generations across flush, and a reusable managed render array.
- JavaScript targets: persistent `AudioContext`/node, transferable block queue, returned
  `ArrayBuffer` pool, two-stage prefill, and processor diagnostics.
- Demo targets: decoded-track caching/prefetch and seek through the player's flush path.
- Existing convention remains NUnit with a deterministic fake bridge; browser-only behavior is
  validated by a browser build and live Chrome lifecycle/diagnostic checks.

### Acceptance Checklist

- [x] Preparation is idempotent and reports actual latency/sample rate.
- [x] Interactive, balanced, playback, and custom buffer configurations select expected sizes.
- [x] The first transfer is bounded independently from the full target buffer, then immediately
  requests a second-stage fill.
- [x] Direct `ISampleProvider` input renders without a wave-provider adapter.
- [x] Device-rate preparation updates output format and resamples without changing pitch duration.
- [x] Flush and seek preserve playback state and avoid a second graph start.
- [x] First-frame and underrun diagnostics ignore stale runs and expose metrics.
- [x] Stop preserves prepared resources while Dispose closes the bridge.
- [x] Browser JavaScript parses, the browser target builds, and the live graph supports
  prepare/play/pause/resume/flush/stop/replay.
- [x] Decoder and transport `MemoryView` boundaries accept array-like views without relying on
  TypedArray-only helpers.

The live Edge measurement on the local source demo reported an estimated fresh-run
start-to-output latency of approximately 8.6 ms. A warm replay measured approximately
27.8 ms under automation; browser scheduling and the physical output device remain
environment-dependent. The AudioContext/AudioWorkletNode identity stayed constant across
stop and replay, and no application console errors were observed.

## 2026-07-31 MP3 EncodingError Regression

### Bounded Target Inventory

- `samples/BrowserMusicPlayerDemo/wwwroot/music-decoder.js`: copies compressed bytes from a
  managed `Span<byte>` before calling `decodeAudioData`.
- `src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet.js`: copies rendered PCM bytes
  from the same `JSType.MemoryView` boundary.
- `tests/javascript/music-decoder.test.mjs` and `audio-worklet-transport.test.mjs`: JavaScript
  boundary models and regression assertions.

The .NET 10 browser runtime marshals `Span<byte>` as a `MemoryView` object with `copyTo`, `set`,
and `slice`; it is not numerically indexed like a TypedArray. Passing that object as the source
to `Uint8Array.set` converts missing numeric properties to zero. The user's MP3 files have valid
ID3/MPEG headers, and a representative file decodes successfully when its original bytes are
passed directly to Edge's `decodeAudioData`.

### Acceptance Checklist

- [x] `MusicPlayerDemo无法加载mp3音乐文件，抛出EncodingError`: compressed MP3 bytes are copied
  through `MemoryView.copyTo` and remain byte-identical before browser decoding.
- [x] The AudioWorklet transport uses the same correct source-memory copy path for PCM.
- [x] TypedArray/array-like fallback behavior remains available for tests and compatible runtimes.
- [x] A real MP3 from `F:\12312313` decodes through the final MusicPlayerDemo module in Edge.

## 2026-07-31 Exact AudioWorklet Consumption Progress (superseded draft; corrected section below)

### Bounded Target Inventory

- `BrowserAudioWorkletPlayer.ConsumedFrameCount`: new public synchronous `long` property used
  to derive playback time from `ConsumedFrameCount / OutputWaveFormat.SampleRate`.
- `IAudioWorkletBridge` and `tests/NAudio.BrowserAudioWorklet.Tests/FakeAudioWorkletBridge.cs`:
  deterministic managed seam for exact reads, unsupported-browser behavior, and lifecycle resets.
- `naudio-audio-worklet.js`: main-thread SharedArrayBuffer allocation and 64-bit seqlock reader.
- `naudio-audio-worklet-processor.js`: audio-thread writer that publishes frames actually copied
  from queued source blocks after process quanta.
- `tests/javascript/audio-worklet-transport.test.mjs` and
  `tests/javascript/audio-worklet-processor.test.mjs`: Node-level shared-state contract tests.

Existing conventions remain NUnit 4 with `Assert.That`/`Assert.Multiple` and a deterministic
fake bridge for managed behavior, plus `node:test` with strict/deep assertions for JavaScript.
No timing sleeps, browser process, external service, or full-workspace build is required for this
bounded test task.

The required polyglot static source-to-test pairing analyzer was attempted once, but the local
Python environment does not contain `tree-sitter-language-pack`. Target pairing is therefore
recorded from the explicit request and existing repository layout, not presented as coverage
evidence.

### Acceptance Checklist

- [ ] `新增 public long ConsumedFrameCount，同步、可频繁读取`: repeated synchronous reads return
  the exact 64-bit bridge value.
- [ ] `表示当前/最近一次 AudioWorklet 运行实际从源队列复制到输出的采样帧数`: processor tests
  count copied frames, not interleaved scalar samples.
- [ ] `不计欠载静音`: an underfilled and a fully empty render quantum do not increase the count.
- [ ] `新 Start/Flush 重置为 0`: both managed lifecycle and processor/transport run state reset.
- [ ] `Pause/Stop/自然结束保留当时精确值`: managed tests cover all three transitions, and the
  processor retains its last published value when stopped or drained.
- [ ] `SharedArrayBuffer + Atomics 在处理器线程更新`: processorOptions receives a shared
  four-word `Int32Array`, and processor writes are observed through atomics.
- [ ] `Int32 seqlock [sequence, runId, low32, high32]`: tests assert the exact layout, an even
  stable sequence, and a value crossing the unsigned low-word boundary.
- [ ] `transport getter returns 0 when graph runId and shared run differ`: a stale shared run is
  rejected instead of leaking the previous run's progress.
- [ ] `不支持 SharedArrayBuffer 时 getter 必须明确抛 NotSupportedException，绝不返回近似值`:
  playback setup remains possible while the managed property throws explicitly.
- [ ] Only `tests/**` and `.testagent/**` are edited, and validation uses the narrow NUnit project
  plus the two focused Node test files.

## 2026-07-31 AudioWorklet Total Consumption Progress — Corrected Research

### Bounded Target Inventory

- `BrowserAudioWorkletPlayer.cs`: public `TotalConsumedFrameCount`, `TotalConsumedSampleCount`,
  `TotalConsumedTime`, and `ResetTotalConsumedAsync`; output channels and prepared output sample
  rate are part of the conversion contract.
- `IAudioWorkletBridge.cs` plus `FakeAudioWorkletBridge.cs`: exact synchronous bridge reads and an
  explicit asynchronous reset seam, with lifecycle, disposal, and failure controls.
- `naudio-audio-worklet-processor.js`: source-frame counter, three-word shared seqlock
  `[sequence, low, high]`, fallback per-quantum snapshots, reset acknowledgements, and final stop
  snapshots.
- `naudio-audio-worklet.js`: stable shared capture/baseline, fallback snapshots, reset correlation,
  final stopped value, and node-id isolation.
- Focused tests: `BrowserAudioWorkletPlayerTotalConsumedTests.cs`,
  `audio-worklet-processor.test.mjs`, and `audio-worklet-transport.test.mjs`.

Conventions are NUnit 4 (`Assert.That`/`Assert.Multiple`) and `node:test` with strict/deep
assertions. No sleeps, browser process, external service, or clock interpolation is used.

The required pairing analyzer was run once with `--include-tested` and completed successfully:
111 source files, 10 test files, 24 statically paired source files. Generated build artifacts make
most remaining entries untested; the target player, worklet modules, and focused tests are paired.
This is static pairing evidence, not line coverage.

### Corrected Acceptance Checklist

- [x] Four public APIs are covered by `TotalConsumedProperties_NewUninitialisedPlayer_ReturnZero`,
  `TotalConsumedProperties_StereoOutput_UseFramesChannelsAndOutputSampleRate`,
  `ResetTotalConsumedAsync_ExplicitReset_ClearsAllTotalsExactlyOnce`, and
  `TotalConsumedApis_AfterDispose_ThrowObjectDisposedException`.
- [x] Frame/sample arithmetic is pinned by `TotalConsumedProperties_StereoOutput_UseFramesChannelsAndOutputSampleRate`
  and `processor counts stereo frames across blocks and partial quanta without counting underrun silence`.
- [x] Actual output sample-rate conversion is pinned by `TotalConsumedTime_DeviceSampleRate_UsesPreparedOutputRate`.
- [x] Copied source (including zero-valued source) counts, queued data and underrun silence do not,
  in `processor counts stereo frames across blocks and partial quanta without counting underrun silence`.
- [x] Play/Pause/Stop/Flush/Seek/natural-end preservation is pinned by
  `LifecycleOperations_DoNotResetTotalConsumedProgress` and
  `processor preserves cumulative totals across start flush stop and drain and reports the final stop value`.
- [x] Shared `[sequence, low, high]`, stable odd-sequence fallback, low-word carry, and signed-64
  saturation are covered by the shared processor and transport tests.
- [x] Fallback snapshots, repeated exact reads, stop final value, concurrent reset acknowledgements,
  stale node isolation, graph failure, and disposal are covered by the focused transport tests.
- [x] Only `tests/**` and `.testagent/**` were changed by this testing subtask; narrow commands are
  recorded in `.testagent/status.md`.
