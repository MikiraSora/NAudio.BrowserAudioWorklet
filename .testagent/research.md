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
