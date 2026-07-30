[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageProject = Join-Path $repoRoot 'src\NAudio.Avalonia.BrowserAudioWorklet\NAudio.Avalonia.BrowserAudioWorklet.csproj'
$consumerProject = Join-Path $repoRoot 'samples\BrowserAudioWorkletPackageDemo\BrowserAudioWorkletPackageDemo.csproj'
$packageOutput = Join-Path $repoRoot 'artifacts\packages'
$consumerCache = Join-Path $repoRoot 'artifacts\package-demo-cache'
$publishOutput = Join-Path $repoRoot 'artifacts\package-demo-publish'

New-Item -ItemType Directory -Force -Path $packageOutput | Out-Null

if (Test-Path -LiteralPath $consumerCache) {
    $resolvedCache = (Resolve-Path -LiteralPath $consumerCache).Path
    if (-not $resolvedCache.StartsWith($repoRoot + '\artifacts\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a cache outside this repository: $resolvedCache"
    }
    Remove-Item -LiteralPath $resolvedCache -Recurse -Force
}

if (Test-Path -LiteralPath $publishOutput) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishOutput).Path
    if (-not $resolvedPublish.StartsWith($repoRoot + '\artifacts\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear publish output outside this repository: $resolvedPublish"
    }
    Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
}

dotnet restore $packageProject
dotnet pack $packageProject -c Release --no-restore -o $packageOutput
dotnet restore $consumerProject --force-evaluate --no-cache
dotnet build $consumerProject -c Release --no-restore

$assetsPath = Join-Path (Split-Path $consumerProject) 'obj\project.assets.json'
$assets = Get-Content -Raw -LiteralPath $assetsPath | ConvertFrom-Json
$packageLibrary = $assets.libraries.PSObject.Properties |
    Where-Object { $_.Name -eq 'NAudio.Avalonia.BrowserAudioWorklet/0.1.0' } |
    Select-Object -ExpandProperty Value
if ($null -eq $packageLibrary -or $packageLibrary.type -ne 'package') {
    throw "The package demo did not resolve NAudio.Avalonia.BrowserAudioWorklet/0.1.0 as a NuGet package."
}

dotnet publish $consumerProject -c Release --no-restore -o $publishOutput
Write-Host "Verified NuGet package consumer: $consumerProject"
