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
