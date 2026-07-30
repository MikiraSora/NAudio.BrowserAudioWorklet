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

## Residual Test Boundary

The browser-only `JSImport` bridge and AudioWorklet processor are not executed by
the NUnit suite because they require WebAssembly. They are covered by the browser
build, NuGet package asset checks, package-only publish, and the live Chrome run.
