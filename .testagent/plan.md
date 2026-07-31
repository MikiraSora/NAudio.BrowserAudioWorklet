# BrowserAudioWorklet Independent Repository Plan

## Completed Work

- [x] Keep `BrowserAudioWorkletPlayer` aligned with NAudio's `IWavePlayer` contract.
- [x] Keep the deterministic fake bridge and wave provider tests.
- [x] Move the library, tests, source-reference Demo, and test notes out of the
  NAudio source tree.
- [x] Replace every direct `NAudio.Core` project reference with the stable
  `NAudio.Core` 2.3.0 NuGet package.
- [x] Add independent package metadata, README, solution, Git ignore rules, and
  a local package validation script.
- [x] Add a package-only Avalonia Browser Demo with no project reference to the
  library.
- [x] Build the package consumer from the local `.nupkg`, verify its assets file,
  and publish it with the package's static web assets.
- [x] Run the full NUnit suite and exercise Play, Pause, Resume, and Stop in Chrome
  using the package-only Demo.
- [x] Add latency profiles, persistent graph preparation, primed first-block startup, direct
  sample-provider rendering, native-device-rate resampling, graph-preserving flush/seek, and
  first-frame/underrun telemetry.
- [x] Recycle transferable AudioWorklet blocks, cache decoded music tracks, prefetch the next
  track, and make all browser `MemoryView` copies compatible with array-like projections.
- [x] Add platform-neutral latency/state tests and Node-based transport, processor, and decoder
  tests.

## Residual Test Boundary

The browser-only `JSImport` bridge and AudioWorklet processor are not executed by
the NUnit suite because they require WebAssembly. They are covered by Node module tests,
the browser build, NuGet package asset checks, package-only publish, and the live Chrome run.

## Low-Latency Implementation Test Plan

| Requirement | Planned evidence |
| --- | --- |
| Preparation and measured latency | `PrepareAsync_IsIdempotentAndPublishesLatencyInfo` |
| Latency profiles and initial transfer | `LatencyProfile_UsesExpectedBufferAndInitialFrames` |
| Direct sample-provider path | `InitSampleProvider_RendersDirectlyIntoBridgeBuffer` |
| Device-native sample rate | `PrepareAsync_DeviceRateUpdatesOutputFormatAndResamples` |
| Flush without graph rebuild | `FlushAsync_WhilePlayingFlushesBridgeWithoutRestart` |
| Seek without graph rebuild | `SeekAsync_SeekableProviderChangesPositionAndFlushes` |
| First-frame diagnostics | `FirstFrameEvent_CurrentRunPublishesEstimatedOutputTime` |
| Underrun diagnostics and metrics | `UnderrunEvent_CurrentRunReportsMissingFrames` and `GetPlaybackMetricsAsync_ForwardsBridgeMetrics` |
| Stale-run isolation | `DiagnosticFromPreviousRun_DoesNotReachCurrentRun` |
| Stop versus Dispose lifecycle | existing stop/dispose tests plus prepare-count assertions |
| Browser transport | Three Node tests, JS syntax checks, browser-target Release build, package publish, and live Chrome lifecycle check |

Implementation order: update the fake bridge, add focused NUnit cases, build/test the test
project, perform an inline gap/assertion review, then run solution and browser validation.

## MP3 EncodingError Fix Plan

| Requirement | Planned evidence |
| --- | --- |
| Preserve compressed bytes from .NET `MemoryView` | `decoder copies compressed bytes through MemoryView.copyTo before decoding` |
| Preserve rendered PCM bytes from .NET `MemoryView` | `transport copies MemoryView bytes before transferring sample blocks` |
| Retain fallback compatibility | Existing array-like cases remain covered in both JavaScript test files |
| Decode an actual user MP3 | Live Edge module decode using `F:\12312313\Fantasy Kaleidoscope ~The Memories of Phantasm~.mp3` |

Implementation order: replace source-side `TypedArray.set(memoryView)` with a small helper that
prefers `memoryView.copyTo(target)`, update the test doubles to match the real .NET 10 contract,
run focused Node tests, then build and validate the browser demo with an actual MP3.

## Exact Consumption Progress Test Plan (superseded draft; corrected plan below)

| Requirement | Planned evidence |
| --- | --- |
| `新增 public long ConsumedFrameCount，同步、可频繁读取` | `ConsumedFrameCount_RepeatedReads_ReturnExact64BitBridgeValue` |
| `表示当前/最近一次 AudioWorklet 运行实际从源队列复制到输出的采样帧数` | `processor publishes exact consumed frame count per quantum without counting underrun silence` |
| `不计欠载静音` | Same processor test asserts partial and empty quantum totals remain at the copied-frame count |
| `新 Start/Flush 重置为 0` | `ConsumedFrameCount_NewStartAndFlush_ResetToZero` and `processor resets count for each start and flush run` |
| `Pause/Stop/自然结束保留当时精确值` | `ConsumedFrameCount_PauseStopAndNaturalEnd_PreserveExactValue` plus processor stop/drain assertions |
| `SharedArrayBuffer + Atomics 在处理器线程更新` | processor shared-state test and `transport exposes exact 64-bit seqlock count and rejects stale runs` |
| `Int32 seqlock [sequence, runId, low32, high32]` | `processor publishes both 32-bit words when consumed frames cross the low-word boundary` plus the transport reader test assert all four indices and a value above `uint.MaxValue` |
| `transport getter returns 0 when graph runId and shared run differ` | `transport exposes exact 64-bit seqlock count and rejects stale runs` |
| `不支持 SharedArrayBuffer 时 getter 必须明确抛 NotSupportedException，绝不返回近似值` | `ConsumedFrameCount_UnsupportedBridge_ThrowsNotSupportedException` and `transport keeps playback available without SharedArrayBuffer and count getter fails explicitly` |
| Test-only scope and narrow validation | Git diff path audit; focused `dotnet test` and two-file `node --test` commands |

Implementation order: extend the fake bridge contract, add focused NUnit lifecycle/property tests,
add processor seqlock/counting tests, add transport reader/unsupported tests, run the narrow suites,
then perform pseudo-mutation and assertion-quality review and record the final clean results.

## AudioWorklet Total Consumption Progress — Corrected Plan

| Requirement | Concrete test evidence |
| --- | --- |
| Four public cumulative APIs and zero initialization | `TotalConsumedProperties_NewUninitialisedPlayer_ReturnZero`; `TotalConsumedProperties_RepeatedReads_ReturnExactStable64BitValues`; `ResetTotalConsumedAsync_ExplicitReset_ClearsAllTotalsExactlyOnce` |
| Frames independent of channels; samples = frames × channels | `TotalConsumedProperties_StereoOutput_UseFramesChannelsAndOutputSampleRate`; `processor counts stereo frames across blocks and partial quanta without counting underrun silence` |
| Time uses actual output/device sample rate | `TotalConsumedTime_DeviceSampleRate_UsesPreparedOutputRate` |
| Copied source only; source silence counts; underrun silence does not | `processor counts stereo frames across blocks and partial quanta without counting underrun silence` |
| Play, Pause, Stop, Flush, Seek, natural-end preserve cumulative value | `LifecycleOperations_DoNotResetTotalConsumedProgress`; `processor preserves cumulative totals across start flush stop and drain and reports the final stop value` |
| Shared three-word Atomics seqlock and exact 64-bit boundaries | `processor publishes a stable three-word shared snapshot across the unsigned low-word boundary`; `processor saturates the cumulative counter at the signed 64-bit maximum`; `transport shared-memory path captures exact 64-bit totals and resets only its baseline` |
| Fallback exact snapshots and stopped final message | `transport fallback snapshots remain exact and stop waits for the final processor value` |
| Reset ack ordering and post-reset value | `processor acknowledges ordered explicit resets and resumes counting from zero`; `transport fallback orders concurrent resets and resolves each matching acknowledgement` |
| Failure/disposal and stale-node isolation | `transport isolates stale node snapshots and rejects pending resets on failure or disposal` |
| Disposed managed API behavior | `TotalConsumedApis_AfterDispose_ThrowObjectDisposedException` |
| Scope/commands | `git diff --check`; focused `dotnet test` and `node --test`; both edited JS test files pass `node --check` |

Implementation and review sequence completed. Source mutation injection was not performed because
this subtask is explicitly forbidden from editing `src/**`; candidate mutations were checked
statically against exact assertions and results recorded in `.testagent/status.md`.

## 2026-07-31 LatencyMeasureHelper Test Plan

This plan is limited to the new latency-measurement contract. It follows the repository's NUnit
fake-bridge and `node:test` conventions. The implementation agent should add the production seam
and tests in the phases below; this research turn intentionally makes no source or test edits.

### Phase 1: managed API and lifecycle tests

Target file: `tests/NAudio.BrowserAudioWorklet.Tests/LatencyMeasureHelperTests.cs`.
Use an injected `IAudioWorkletBridge` through an internal helper core/overload and a short test
deadline. The scripted bridge should drive the real `BrowserAudioWorkletPlayer`, not a fake player,
so option validation, source rendering, events, run state, and disposal are exercised together.

| Requirement from the feature plan | Proposed NUnit test | Required assertions |
| --- | --- | --- |
| `public static Task<TimeSpan> MeasureLatency(BrowserAudioWorkletOptions options)` and no `AudioWorkletOptions` alias | `MeasureLatency_PublicApi_UsesBrowserAudioWorkletOptionsAndReturnsTaskOfTimeSpan` | Reflection/compile-time shape is exact; helper is public static; no alias type is present. |
| `null` options throws `ArgumentNullException` | `MeasureLatency_NullOptions_ThrowsArgumentNullExceptionBeforePlatformCheck` | Exact exception and `ParamName == "options"`, even on the plain `net10.0` test target. |
| Invalid option uses player validation | `MeasureLatency_InvalidOptions_UsesExistingPlayerValidation` | Parameterized invalid buffer durations and initial frame counts raise `ArgumentOutOfRangeException`; injected bridge is disposed if construction fails. |
| Non-browser/WASM target throws `PlatformNotSupportedException` | `MeasureLatency_OutsideBrowser_ThrowsPlatformNotSupportedException` | Valid options on `net10.0` fail synchronously with the platform exception. |
| Strictly reuse the passed options | `MeasureLatency_ForwardsExactOptionsToTheSinglePreparedPlayer` | Bridge sees the custom buffer duration, initial frame count, and `UseDeviceSampleRate` values on one preparation. |
| One temporary player/context/node, six total runs, deterministic release | `MeasureLatency_ReusesOnePreparedPlayerAcrossSixRunsAndDisposesItOnce` | `PrepareCount == 1`, `StartCount == 6`, all callbacks belong to one bridge/player, and `DisposeCount == 1` after success. |
| Fixed 48 kHz, stereo, 440 Hz, gain 0.2, 100 ms probe; reset each run | `MeasureLatency_UsesFixedProbeAndResetsSourceBeforeEveryRun` | Requested source format and volume-independent sample values match the sine contract; each run renders exactly 4,800 frames/9,600 samples and has the same initial/full sequence. |
| Discard one warmup, average five formal runs | `MeasureLatency_ExcludesWarmupAndReturnsArithmeticMeanOfFiveRuns` | Script `[warmup, m1, m2, m3, m4, m5]`; exact result equals `(m1 + ... + m5) / 5`, start count is six, and warmup cannot influence the result. |
| Wait for both first-frame and natural-stop per run | `MeasureLatency_WaitsForBothFirstFrameAndNaturalStopBeforeAdvancing` | Holding either callback prevents the next run; releasing both permits the next start, and all six runs complete. |
| Missing first-frame within 10 seconds is a timeout | `MeasureLatency_MissingFirstFrame_ThrowsTimeoutExceptionAndDisposes` | Script natural stop only with an injected short deadline; assert `TimeoutException`, no second run, and deterministic disposal. |
| Missing natural-stop within 10 seconds is a timeout | `MeasureLatency_MissingNaturalStop_ThrowsTimeoutExceptionAndDisposes` | Script first-frame only with an injected short deadline; assert `TimeoutException`, no second run, and deterministic disposal. |
| Autoplay/resume/start errors propagate without an extra wrapper | `MeasureLatency_AutoplayStartFailure_PropagatesOriginalBrowserAudioExceptionAndDisposes` | A scripted `BrowserAudioException` from start/resume is the same instance observed by the caller; bridge/player is released once. |
| Other transport/natural-stop errors propagate | `MeasureLatency_NaturalStopFailure_PropagatesOriginalBrowserAudioExceptionAndDisposes` | A first-frame followed by a scripted stop error returns that same error rather than a timeout or helper wrapper. |
| Internal timing field reaches the helper without new public diagnostics | `FirstFrameEvent_ObservedResumeLatencyRemainsInternalAndIsConsumedByHelper` | The helper returns the injected observed field; reflection confirms no public `ObservedResume...` property was added to `BrowserAudioFirstFrameEventArgs`. |

The source-reset test should collect samples through the fake bridge's render callback rather than
checking a private provider position. This catches a provider that is only reset for the warmup or
that emits the correct duration but stale phase on later runs.

### Phase 2: JavaScript transport tests

Target file: `tests/javascript/audio-worklet-transport.test.mjs`. Extend the existing fake context
with a controllable `performance.now()` and resume hook. Each test must use a unique graph handle and
dispose it so pending promises and graph state do not leak between tests.

| Requirement from the feature plan | Proposed Node test | Required assertions |
| --- | --- | --- |
| Record the timestamp immediately before `AudioContext.resume()` and attach observed first-frame latency | `transport records resume time before context.resume and reports observed first-frame latency` | Controlled clock advances inside fake `resume`; reported observed value uses the pre-resume timestamp, and the first-frame event/metrics expose it. |
| Preserve `EstimatedStartToOutputLatencySeconds` semantics | Same test | Existing output estimate remains based on `runStartPerformanceTime` and output-latency fallback; assert its prior value separately from the new observed value. |
| Different run generations do not reuse timestamps | `transport isolates observed resume latency between run generations` | Begin/resume run 1 and run 2 with different clock values; a run-1 first-frame is ignored, and run 2 reports only its own elapsed value. |
| Old AudioWorkletNode messages do not pollute a replacement node | `transport ignores stale-node first-frame messages after node replacement` | Force `onprocessorerror`, start the replacement node, emit a first-frame from the old node, and assert only the replacement event resolves/updates metrics. |

No processor behavior change is needed for this measurement; the processor's existing first-frame
message is the source event. Keep the existing processor tests intact and run `node --check` on both
shipping worklet files.

### Phase 3: documentation and integration checks (superseded)

The audible-probe and no-Demo-change requirements in this phase are superseded by the addendum
below. Retain the timing-boundary documentation, but require muted output and add the Demo trigger.

### Suggested minimum commands

After implementation, the narrowest relevant commands are:

```powershell
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release --filter "FullyQualifiedName~LatencyMeasureHelperTests"
node --test .\tests\javascript\audio-worklet-transport.test.mjs
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet.js
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet-processor.js
```

The requested clean acceptance run is:

```powershell
dotnet build .\NAudio.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release
node --test .\tests\javascript\*.test.mjs
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet.js
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet-processor.js
.\eng\Test-Package.ps1
```

The focused command is expected to include all existing player tests only when the filter is
omitted; the class filter above keeps the first fix cycle bounded. The package script is the
repository's existing local NuGet consumer check and should be run after the focused suites pass.

## 2026-07-31 BrowserAudioWorkletPlayer Context Latency Properties Test Plan Addendum

This addendum covers the public `BrowserAudioWorkletPlayer.BaseLatency` and `OutputLatency`
properties. It is bounded to the existing fake bridge and latency fixture; no new JavaScript
behavior is required because the preparation payload already contains both browser values.

### Managed tests

Target: `tests/NAudio.BrowserAudioWorklet.Tests/BrowserAudioWorkletPlayerLatencyTests.cs`.
Reuse `FakeAudioWorkletBridge.BaseLatencySeconds` and `OutputLatencySeconds` so assertions stay
deterministic and do not require a browser.

| Requirement | Exact test | Required assertions |
| --- | --- | --- |
| Public names, CLR types, and read-only shape | `AudioContextLatencyProperties_AreReadOnlyAndZeroBeforePreparation` | Reflection finds `BaseLatency` and `OutputLatency`, each has `PropertyType == typeof(double)`, `CanRead == true`, and `CanWrite == false`. |
| Pre-prepare semantics | `AudioContextLatencyProperties_AreReadOnlyAndZeroBeforePreparation` | A new initialized player reports `0.0` for both properties before `PrepareAsync`; no bridge preparation is needed to read the defaults. |
| Prepared values and seconds units | `AudioContextLatencyProperties_AreReadOnlyAndZeroBeforePreparation` | Configure `0.006` and `0.014` on the fake, prepare once, and assert exact property values. Decimal values catch an accidental milliseconds conversion. |
| Existing snapshot/idempotence behavior | `PrepareAsync_IsIdempotentAndPublishesLatencyInfo` | Configure `0.004` and `0.012`, call `PrepareAsync` twice, assert `PrepareCount == 1`, the returned `BrowserAudioLatencyInfo` is the same instance, and both direct properties match the snapshot. |

If the implementation chooses a separate test for API shape, it may split the first row, but the
observable assertions must remain identical. Do not add a public property to
`BrowserAudioFirstFrameEventArgs` or change `EstimatedStartToOutputLatencySeconds`.

### Documentation updates

Update `README.md` and `src/NAudio.BrowserAudioWorklet/README.md` in the existing latency/diagnostic
sections. Include a short code example that reads `output.BaseLatency` and `output.OutputLatency`,
state that both are read-only seconds values copied from the prepared `AudioContext`, and state
that they return zero before `PrepareAsync` completes. Preserve the existing explanation that
these context values do not represent the physical time a speaker produces sound. Mention the
existing JavaScript non-finite fallback as zero only where the surrounding diagnostics text needs
that qualification; do not introduce a second latency naming convention.

### Verification commands

Run the focused managed fixture first:

```powershell
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release --filter "FullyQualifiedName~BrowserAudioWorkletPlayerLatencyTests"
```

Then run the repository acceptance checks, including the documentation-facing package consumer:

```powershell
dotnet build .\NAudio.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release
node --test .\tests\javascript\*.test.mjs
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet.js
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet-processor.js
.\eng\Test-Package.ps1
git diff --check
```

The focused test and static pairing scan are the evidence for the new managed API contract; the
Node checks ensure the unchanged transport still reports the same preparation latency fields, and
the package script verifies that the public properties are available to a consumer. This planning
addendum itself does not claim that those commands have been run.

## 2026-07-31 Silent Probe and BrowserAudioWorkletDemo Test Plan Addendum

This addendum supersedes the prior audible browser acceptance. This planning turn does not edit
production or test sources.

### Phase A: lock the silent-output boundary

Extend the test-only `LatencyMeasureBridge` in
`tests/NAudio.BrowserAudioWorklet.Tests/LatencyMeasureHelperTests.cs` so `SetVolume` records a
history and `StartAsync` records the most recent volume at each run start.

| User requirement | Exact proposed test | Assertions |
| --- | --- | --- |
| The latency probe must be silent and must not send audible volume to output | `MeasureLatency_UsesZeroOutputGainBeforeEveryRunWhileRenderingNonZeroProbeFrames` | All six `VolumeAtStart` values are exactly `0.0f`; volume history contains no positive value; rendered PCM still contains the expected non-zero 440 Hz sample; `StartCount == 6` and the average remains correct. |
| Muting must not change the established six-run lifecycle | Existing `MeasureLatency_AveragesFiveRunsAfterWarmup_UsesOnePlayerAndResetsProbe` plus the new mute test | One prepare, six starts, five-value mean, per-run reset, and one dispose remain unchanged. |

The combined positive/negative assertion is essential: zero output gain proves silence, while
non-zero PCM proves the test did not pass merely because the probe source was replaced with zeros.
The volume must be captured at `StartAsync`, which is the last managed boundary before the bridge
calls `AudioContext.resume()`.

### Phase B: Demo command and compiled binding

The preferred production seam is an internal constructor/delegate on `MainViewModel`, defaulting to
`LatencyMeasureHelper.MeasureLatency`, so command behavior can be tested without a browser. The
command should use the existing `AsyncCommand` execution guard and `Status` surface unless a
separate `LatencyStatus` property is deliberately introduced.

| User requirement | Exact proposed evidence | Assertions |
| --- | --- | --- |
| The Demo button invokes `MeasureLatency` and displays the result | `MainViewModel_MeasureLatencyCommand_InvokesInjectedHelperOnceAndDisplaysMilliseconds` | The injected delegate is called once with the intended options; status first reports measurement in progress, then contains the exact formatted non-negative millisecond result; the command is executable again. |
| Demo failure is visible and command state recovers | `MainViewModel_MeasureLatencyCommand_WhenHelperFails_DisplaysRootMessageAndReenablesCommand` | The root exception message is shown, no success text remains, and `CanExecute` returns true after completion. |
| The visual button and result use compiled bindings | `MainView_MeasureLatencyButton_UsesCompiledCommandAndStatusBindings` (headless if a Demo test target is added) plus the Demo Release build | The named button resolves, its `Command` is the ViewModel measurement command, and the bound status/result text changes after execution. The XAML compiler rejects missing command/status properties. |

There is currently no runnable automated target for these three Demo tests: the sample is
`net10.0-browser` only and the existing NUnit project is plain `net10.0`. To implement them, add a
small platform-neutral Demo test target using Avalonia Headless NUnit and an injected measurement
delegate, or source-link the ViewModel/view into such a target. Until that seam exists, treat the
two ViewModel tests and the headless binding test as planned (not passing evidence), use the
compiled-XAML Release build as the automated wiring check, and perform the browser scenario below.

### Browser acceptance scenario

Scenario name: `BrowserAudioWorkletDemo_MeasureLatencyButton_RunsSilentlyAndDisplaysResult`.

1. Start the Demo, authorize Web Audio with the existing playback control if the browser requires
   it, then stop normal playback so no unrelated tone is present.
2. Click the new latency-measurement button once.
3. Verify the button/command enters a busy state and the status reports measurement in progress.
4. Hear no probe tone during all six runs.
5. Verify the final status displays a finite, non-negative value in milliseconds and the button is
   enabled again.
6. Verify the browser console has no application error and the temporary AudioContext is closed.

### Focused and acceptance commands

After the mute regression test is implemented:

```powershell
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release --filter "Name~MeasureLatency_UsesZeroOutputGainBeforeEveryRunWhileRenderingNonZeroProbeFrames"
dotnet build .\samples\BrowserAudioWorkletDemo\BrowserAudioWorkletDemo.csproj -c Release
```

If the planned Avalonia headless Demo test target is added:

```powershell
dotnet test --project .\tests\BrowserAudioWorkletDemo.Tests\BrowserAudioWorkletDemo.Tests.csproj -c Release
```

Final regression commands remain:

```powershell
dotnet build .\NAudio.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.BrowserAudioWorklet.Tests\NAudio.BrowserAudioWorklet.Tests.csproj -c Release
node --test .\tests\javascript\*.test.mjs
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet.js
node --check .\src\NAudio.BrowserAudioWorklet\wwwroot\naudio-audio-worklet-processor.js
.\eng\Test-Package.ps1
```
