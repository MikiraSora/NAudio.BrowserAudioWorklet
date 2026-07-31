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

## 2026-07-31 LatencyMeasureHelper Research

### Bounded target inventory

- Planned production type: `src/NAudio.BrowserAudioWorklet/LatencyMeasureHelper.cs` in
  namespace `NAudio.Wave.Browser`. It was absent when this Research pass began; any implementation
  now visible in the shared working tree belongs to the parallel implementation agent.
- Managed transport seam: `BrowserAudioWorkletPlayer.cs`, `IAudioWorkletBridge.cs`,
  `BrowserAudioDiagnostics.cs`, and `BrowserAudioWorkletOptions.cs`.
- Existing managed tests: `tests/NAudio.BrowserAudioWorklet.Tests/BrowserAudioWorkletPlayerLatencyTests.cs`,
  `BrowserAudioWorkletPlayerPlaybackTests.cs`, `BrowserAudioWorkletPlayerTests.cs`,
  `FakeAudioWorkletBridge.cs`, and `TestSampleProvider.cs`.
- Main-thread transport: `src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet.js`;
  focused Node tests live in `tests/javascript/audio-worklet-transport.test.mjs`.
- Audio-thread processor: `src/NAudio.BrowserAudioWorklet/wwwroot/naudio-audio-worklet-processor.js`;
  syntax and existing behavior are covered by `tests/javascript/audio-worklet-processor.test.mjs`.
- Documentation boundary: `src/NAudio.BrowserAudioWorklet/README.md`; the existing demos must
  remain unchanged for this feature.

### Static pairing evidence

The required polyglot `find_untested_sources.py --include-tested` scan was run once against the
repository root. It reported 111 source files, 10 test files, 24 statically paired source files,
87 unpaired source files, one orphan test file, and languages `csharp`, `javascript`, and `python`.
The result is a static identifier/import pairing heuristic, not line or branch coverage. Generated
files under `artifacts/` account for most unpaired entries and are outside this bounded task.

Relevant pairings from the report are already present for `BrowserAudioWorkletPlayer.cs` (the four
player test files), `BrowserAudioDiagnostics.cs` and `BrowserAudioWorkletOptions.cs` (the latency
tests), `IAudioWorkletBridge.cs` (the fake bridge), and both shipping worklet modules (the focused
processor/transport Node tests). The new helper has no analyzer entry until its production source
exists; the established test location is `tests/NAudio.BrowserAudioWorklet.Tests/LatencyMeasureHelperTests.cs`.

### Existing conventions and constraints

- The test project targets plain `net10.0`, uses NUnit 4 with the Microsoft Testing Platform, and
  marks unit fixtures with `[Category("UnitTest")]`. Assertions use `Assert.That` and
  `Assert.Multiple`; asynchronous tests return `Task` and avoid sleeps.
- `NAudio.BrowserAudioWorklet.csproj` grants `InternalsVisibleTo` to the test assembly. The
  internal `BrowserAudioWorkletPlayer(IAudioWorkletBridge, BrowserAudioWorkletOptions)` constructor
  and `IAudioWorkletBridge` are therefore the existing injection seam.
- The public player constructor calls `CreateDefaultBridge()` and throws
  `PlatformNotSupportedException` on the non-browser target. A managed helper test cannot call the
  public constructor on `net10.0`; it needs an internal overload/core that accepts one injected
  `IAudioWorkletBridge` while the public method keeps the browser-only branch.
- `FakeAudioWorkletBridge` already records prepare/start/stop/dispose counts, options-derived
  transport values, render callbacks, stop callbacks, and event callbacks. A deterministic
  scripted hook (or a test-local implementation of `IAudioWorkletBridge`) can render a complete
  probe, emit a first-frame event, and signal natural stop for each run without a browser.
- The current `AudioWorkletEvent` carries first-frame context time and the existing
  start-to-output estimate. The current `BrowserAudioFirstFrameEventArgs` constructor is internal
  and exposes only output-facing public properties. The new resume-to-first-frame value should be
  an internal field/property (milliseconds or explicitly named seconds) and must not become a
  public diagnostics property.
- `AudioWorkletBridge.EventLoop` reads the first-frame message on the main thread and
  `BrowserAudioWorkletPlayer.OnBridgeEvent` filters stale run IDs before raising the event. The
  helper must subscribe to both `FirstFrameRendered` and `PlaybackStopped`, reuse one player for
  all six runs, and dispose it in success, error, and timeout paths.
- `naudio-audio-worklet.js` currently resets `graph.runId`/metrics in `beginRun`, calls
  `context.resume()` directly from `resume`, calculates the existing
  `startToOutputLatency`, and rejects messages from disposed or stale node IDs/run IDs. A new
  per-run resume timestamp must be cleared by `beginRun`, recorded immediately before
  `context.resume()`, and only used for a matching first-frame message. The existing output
  estimate must remain unchanged.

### Deterministic managed fixture

The proposed bridge script uses six run entries: one warmup value followed by five measured
values. Each entry requests all samples until the source returns zero, emits a first-frame event
with a controlled observed latency, and invokes the natural-stop callback. It records every start,
rendered sample sequence, option-derived buffer setting, and disposal call. This allows tests to
prove that the source was reset before every run, that six runs occurred on one prepared graph, and
that the warmup value is excluded from the arithmetic mean. Timeout scripts intentionally omit one
of the two required callbacks and use an internal short deadline; the public browser deadline
remains 10 seconds.

The fixed source contract to assert is 48,000 Hz, two channels, 440 Hz sine, gain 0.2, and exactly
100 ms (4,800 frames / 9,600 interleaved samples) per run. Comparing the first samples and full
sequences of consecutive runs proves reset behavior without relying on wall-clock audio output.

### Acceptance checklist for this feature

- [ ] Public API is exactly `public static Task<TimeSpan> MeasureLatency(BrowserAudioWorkletOptions options)`;
  no `AudioWorkletOptions` alias is added.
- [ ] `null` options fail with `ArgumentNullException`; invalid buffer/initial-frame options use
  the existing player validation; valid calls on the non-browser target fail with
  `PlatformNotSupportedException`.
- [ ] The helper forwards the same option values to one temporary player/graph, prepares once,
  starts exactly six runs, and deterministically disposes resources on every exit path.
- [ ] Every run resets the fixed 48 kHz stereo 440 Hz / 0.2 / 100 ms probe source.
- [ ] One warmup is discarded and the returned value is the arithmetic mean of exactly five
  measured `TimeSpan` values.
- [ ] Each run requires both first-frame and natural-stop notifications; missing either event for
  the deadline raises `TimeoutException`.
- [ ] Autoplay/start and natural-stop/transport errors propagate as the original browser error,
  without an extra helper wrapper.
- [ ] JavaScript records `performance.now()` immediately before `AudioContext.resume()`, reports
  the matching run's observed resume-to-first-frame latency, preserves the existing output-latency
  estimate, isolates run generations, and ignores stale-node messages.
- [ ] The new timing value reaches the helper through internal event data only; no extra public
  diagnostics property is exposed.
- [ ] Superseded by the silent-probe and Demo-button addendum below: the earlier requirement for
  six audible tones and no Demo UI change no longer applies.
- [ ] Release build, NUnit, all Node tests, both worklet `node --check` commands, and the local
  NuGet package consumer remain green; superseded browser acceptance is now covered by the silent
  Demo scenario below.

## 2026-07-31 Silent Probe and BrowserAudioWorkletDemo Addendum

This addendum supersedes the earlier audible-probe and no-Demo-change assumptions. The new user
requirements are that latency measurement must not produce audible output and that
`samples/BrowserAudioWorkletDemo` must expose a button which invokes the helper and displays the
result.

### Current implementation findings

- `LatencyMeasureHelper` currently creates a 48 kHz stereo 440 Hz sine provider with source gain
  0.2 and leaves `BrowserAudioWorkletPlayer.Volume` at its unity default. The source therefore
  reaches the Web Audio `GainNode` at an audible level. The XML comments and both README files also
  describe six audible probes.
- Keeping non-zero source samples is useful: the processor still copies real frames and emits its
  normal first-frame message. The correct mute boundary is the temporary player's output gain,
  set to `0.0f` before the first `PlayAsync`/`AudioContext.resume()` call. Muting by replacing the
  source with zero samples would not prove the output gain contract and would weaken the probe.
- `LatencyMeasureHelperTests.MeasureLatency_AveragesFiveRunsAfterWarmup_UsesOnePlayerAndResetsProbe`
  already proves the generated PCM is non-zero, but its `LatencyMeasureBridge.SetVolume` method
  discards every value. There is no assertion that the gain is zero before a run starts or remains
  zero across all six runs.
- `BrowserAudioWorkletDemo.MainViewModel` follows an `INotifyPropertyChanged` + private
  `AsyncCommand` pattern and exposes `Status` as the existing status surface. `MainView.axaml` has
  `x:DataType="local:MainViewModel"`, and the project enables compiled bindings by default, so a
  Release build is a strong check that a new command and result/status binding refer to real CLR
  properties.
- The Demo currently has no test project. It targets only `net10.0-browser`, directly constructs a
  browser player in the ViewModel constructor, and would throw on the existing plain `net10.0`
  NUnit target. A deterministic ViewModel unit test therefore requires an injected measurement
  delegate/service and a platform-neutral test compilation boundary. Without that production seam,
  the honest evidence is compiled-XAML build plus a browser interaction smoke test.

### Updated static pairing evidence

The required polyglot pairing scan was rerun once for this expanded scope. It reported 112 source
files, 11 test files, 25 statically paired source files, 87 unpaired source files, and one orphan
test. `LatencyMeasureHelper.cs` is paired to `LatencyMeasureHelperTests.cs`. Generated files under
`artifacts/` again dominate the unpaired list. The 40-entry unpaired limit was exhausted by those
artifacts, so the scan did not emit a suggested path for `MainViewModel.cs`; independently, the
repository contains no Demo test project or test reference to that type. This remains a static
pairing heuristic, not coverage evidence.

### Revised acceptance checklist

- [ ] The helper renders the existing non-zero 48 kHz stereo probe, but the temporary player's
  output gain is `0.0f` before every start/resume and never returns to unity during measurement.
- [ ] All six runs remain observable to the AudioWorklet and the latency average/lifecycle behavior
  remains unchanged while muted.
- [ ] Public and package README text no longer claims that the probes are audible; browser
  acceptance explicitly expects silence.
- [ ] `BrowserAudioWorkletDemo.MainViewModel` exposes a measurement command that directly invokes
  `LatencyMeasureHelper.MeasureLatency` with explicit options from the button gesture.
- [ ] The command publishes an in-progress state, displays the completed non-negative result in
  milliseconds, and reports the root error message on failure while becoming executable again.
- [ ] `MainView.axaml` contains a visible latency-measurement button and a compiled binding to the
  command and result/status surface.
- [ ] The Demo Release build succeeds, the button works in a browser, measurement is silent, the
  result is displayed, and no AudioContext or console error remains after completion.

## 2026-07-31 BrowserAudioWorkletPlayer Context Latency Properties Addendum

### Bounded target inventory

- The requested public surface is in `src/NAudio.BrowserAudioWorklet/BrowserAudioWorkletPlayer.cs`.
  The exact property names are `BaseLatency` and `OutputLatency`; both are intended to expose
  browser-reported values without adding another options or diagnostics type.
- `AudioWorkletPreparation` in `IAudioWorkletBridge.cs` already carries
  `BaseLatencySeconds` and `OutputLatencySeconds`. `BrowserAudioWorkletPlayer.PrepareCoreAsync`
  copies those fields into the existing `BrowserAudioLatencyInfo` snapshot.
- `AudioWorkletBridge.cs` reads the JavaScript preparation result, while
  `wwwroot/naudio-audio-worklet.js` obtains `AudioContext.baseLatency` and
  `AudioContext.outputLatency`. Its `latencyInfo` helper converts non-finite browser values to
  zero before they cross the interop boundary.
- The managed fake already exposes configurable `BaseLatencySeconds` and
  `OutputLatencySeconds`, so no new bridge seam is required. The focused test file is
  `tests/NAudio.BrowserAudioWorklet.Tests/BrowserAudioWorkletPlayerLatencyTests.cs`.
- Public/package documentation lives in `README.md` and
  `src/NAudio.BrowserAudioWorklet/README.md`; both already document `BrowserAudioLatencyInfo` and
  are the correct locations for the direct property wording.

### Observed contract and selected semantics

- The API must be exactly `public double BaseLatency { get; }` and
  `public double OutputLatency { get; }`, measured in seconds. Reflection must see `System.Double`
  and no public setter for either property.
- Before a successful `PrepareAsync` completes, both getters return `0.0`. After preparation they
  return the corresponding `BrowserAudioLatencyInfo` seconds value, without converting to
  milliseconds or issuing a second JavaScript query. Repeated `PrepareAsync` calls reuse the
  existing preparation snapshot and therefore keep the bridge preparation count at one.
- The existing `LatencyInfo` record and `EstimatedDeviceLatencySeconds` remain the source of truth;
  these properties are convenience projections and must not alter first-frame telemetry or the
  existing output-latency estimate.
- A browser that does not expose a finite latency value is represented as zero by the existing
  JavaScript transport. That transport fallback is distinct from the managed pre-preparation
  default, but both are intentionally observable as zero seconds.

### Static pairing evidence

The required `find_untested_sources.py --include-tested` scan was run once for this scope. It
reported 112 source files, 11 test files, 25 statically paired source files, 87 unpaired source
files, and one orphan test. The relevant pairings are `BrowserAudioWorkletPlayer.cs` with the
player latency/playback tests, `BrowserAudioDiagnostics.cs` and `IAudioWorkletBridge.cs` with the
same latency fixture and fake bridge, and the existing JavaScript transport tests with the
shipping transport module. This is static identifier/import pairing evidence, not coverage.

### Acceptance checklist for this addendum

- [ ] Both public properties are read-only `double` values whose names match `BaseLatency` and
  `OutputLatency` exactly.
- [ ] Values are in seconds, are zero before preparation, and equal the prepared
  `AudioContext` values afterward, including when those values are non-zero decimals.
- [ ] Preparation remains idempotent and the existing `LatencyInfo` and output-estimate semantics
  are unchanged.
- [ ] Both README files show the direct properties, their units, and the zero-before-prepare
  behavior; unsupported/non-finite browser values remain documented as zero.
- [ ] Focused NUnit, Release build, complete Node/worklet checks, and the local package consumer
  are recorded after implementation.
