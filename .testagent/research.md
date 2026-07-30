# BrowserAudioWorklet Independent Repository Research

## Scope

- Production project: `src/NAudio.BrowserAudioWorklet/`.
- Unit-test project: `tests/NAudio.BrowserAudioWorklet.Tests/`.
- Source-reference Demo: `samples/BrowserAudioWorkletDemo/`.
- Package-only Demo: `samples/BrowserAudioWorkletPackageDemo/`.
- Package version under validation: `0.1.0`.

## Repository Conventions

- C# and .NET 9 targets, built by the installed .NET 10 SDK.
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
- The package exposes both `net9.0` and the normalized browser TFM
  `net9.0-browser1.0` in its NuGet layout.
- `Span<byte>` remains the JavaScript memory-view boundary. Stable NAudio.Core
  2.3.0 supplies array-based `ISampleProvider.Read`, so the player reuses a float
  array and copies its bit-identical bytes into that Span.
