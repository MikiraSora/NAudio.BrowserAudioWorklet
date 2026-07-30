# NAudio.Avalonia.BrowserAudioWorklet

`NAudio.Avalonia.BrowserAudioWorklet` adds browser audio output to NAudio through
the Web Audio `AudioWorklet` API. It is intended for Avalonia Browser applications
and also works in other .NET WebAssembly applications.

The package exposes:

| Type | Role |
| --- | --- |
| `BrowserAudioWorkletPlayer` | An `IWavePlayer` that plays any supported NAudio `IWaveProvider` |
| `BrowserAudioException` | A Web Audio failure reported through `PlaybackStopped` |

NAudio names its playback interface `IWavePlayer`; there is no `IAudioPlayer`
interface in NAudio.Core.

## Platform

The real output backend targets `net9.0-browser`. The package also contains a
`net9.0` target so code can reference the type and its state machine can be tested
outside a browser. Calling a public player constructor outside WebAssembly throws
`PlatformNotSupportedException`.

The package carries both JavaScript modules as static web assets. A consuming
WebAssembly application publishes them automatically under:

```text
_content/NAudio.Avalonia.BrowserAudioWorklet/
```

No script tag or manual `AudioWorklet.addModule` call is required.

```powershell
dotnet add package NAudio.Avalonia.BrowserAudioWorklet
```

## Data Flow

```text
IWaveProvider
    -> ISampleProvider (interleaved Float32)
    -> BrowserAudioWorkletPlayer
    -> [JSImport] main-thread transport
    -> transferable ArrayBuffer messages
    -> AudioWorkletProcessor ring buffer
    -> GainNode
    -> speakers
```

The processor asks for more frames when its ring buffer crosses a low-water mark.
Managed code reads only that demand from the source, converts supported PCM or IEEE
floating-point input to interleaved 32-bit floating-point samples, and transfers the
rendered bytes to the audio thread. End of stream drains queued samples before the
Web Audio graph is closed.

## Usage

Create and initialize the player once. Call `PlayAsync` directly from a click or tap
handler so `AudioContext.resume()` remains associated with the browser's user
activation:

```csharp
using NAudio.Wave;
using NAudio.Wave.Browser;

await using var stream = await OpenWaveStreamAsync();
using var reader = new WaveFileReader(stream);
using var output = new BrowserAudioWorkletPlayer();

output.Init(reader);
output.PlaybackStopped += (_, args) =>
{
    if (args.Exception is not null)
    {
        Console.Error.WriteLine(args.Exception.Message);
    }
};

await output.PlayAsync();
```

`Play()` remains available through `IWavePlayer`; it starts the same asynchronous
operation and reports startup failure through `PlaybackStopped`. Prefer `PlayAsync`
when the caller can await it.

The constructor accepts an optional target buffer duration in milliseconds:

```csharp
using var output = new BrowserAudioWorkletPlayer(bufferDurationMilliseconds: 100);
```

The allowed range is 20 to 5000 ms and the default is 250 ms. Larger buffers better
tolerate main-thread stalls; smaller buffers reduce queued audio and memory use.

## Behavior

- `Pause()` suspends the `AudioContext`; `PlayAsync()` resumes the existing graph.
- `Stop()` closes the current graph and raises `PlaybackStopped` with no exception.
- Natural end of stream drains buffered frames, closes the graph, and raises
  `PlaybackStopped` once.
- Web Audio and interop failures are reported as `BrowserAudioException` through
  `PlaybackStopped`. `PlayAsync()` also faults when graph creation or an explicit
  resume from `Paused` fails.
- `Volume` accepts values from `0.0` to `1.0` and updates a Web Audio `GainNode`, so
  already-buffered samples respond immediately.
- Web Audio supports at most 32 channels; initialization rejects wider sources.
- Compressed formats still require a browser-compatible managed decoder. Windows-only
  Media Foundation readers are not available in WebAssembly.

See `samples/BrowserAudioWorkletDemo` for a runnable Avalonia Browser application.
