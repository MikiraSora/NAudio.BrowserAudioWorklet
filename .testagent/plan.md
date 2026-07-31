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
