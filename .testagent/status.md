# BrowserAudioWorklet Test Status

Date: 2026-07-31

## Result

- NUnit/Microsoft Testing Platform: 44 passed, 0 failed, 0 skipped.
- Main solution Release build: library `net9.0` and `net9.0-browser`, tests, and
  source-reference Demo built with 0 warnings and 0 errors.
- NuGet pack: version 0.1.0 produced the library and symbols packages with both
  target assemblies, README, and two AudioWorklet static web assets.
- Package-only validation: restored from `artifacts/packages`, resolved the
  library as a NuGet `package` in `project.assets.json`, built and published the
  Avalonia Browser consumer with 0 warnings and 0 errors.
- Live Chrome: package-only Demo reached `Ready -> Playing -> Paused -> Playing ->
  Stopped`; the published package assets are available under `_content/`.

## Commands

```powershell
dotnet build .\NAudio.Avalonia.BrowserAudioWorklet.slnx -c Release
dotnet test --project .\tests\NAudio.Avalonia.BrowserAudioWorklet.Tests\NAudio.Avalonia.BrowserAudioWorklet.Tests.csproj -c Release --no-build
.\eng\Test-Package.ps1
```
