using System;

namespace NAudio.Wave.Browser;

/// <summary>
/// Thrown or reported when the browser Web Audio graph backing a
/// <see cref="BrowserAudioWorkletPlayer"/> fails - for example the <c>AudioContext</c> or
/// <c>AudioWorklet</c> module could not be created, or the audio thread reported a transport
/// error. The message carries the detail surfaced by the JavaScript side.
/// </summary>
public sealed class BrowserAudioException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="BrowserAudioException"/> class.</summary>
    public BrowserAudioException()
    {
    }

    /// <summary>Initialises a new instance with a descriptive message.</summary>
    /// <param name="message">A description of the failure.</param>
    public BrowserAudioException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance with a message and an inner exception.</summary>
    /// <param name="message">A description of the failure.</param>
    /// <param name="innerException">The underlying cause.</param>
    public BrowserAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
