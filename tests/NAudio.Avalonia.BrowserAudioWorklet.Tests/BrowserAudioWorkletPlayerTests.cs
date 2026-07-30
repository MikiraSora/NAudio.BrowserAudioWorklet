using System;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.Browser;
using NUnit.Framework;

namespace NAudio.Avalonia.BrowserAudioWorklet.Tests;

[TestFixture]
[Category("UnitTest")]
public class BrowserAudioWorkletPlayerTests
{
    private static BrowserAudioWorkletPlayer CreatePlayer(FakeAudioWorkletBridge bridge)
        => new(bridge);

    private static SequenceWaveProvider Pcm16(int sampleRate, int channels, params short[] samples)
        => new(new WaveFormat(sampleRate, 16, channels), samples);

    [Test]
    public void Constructor_DefaultState_IsStoppedAndUninitialised()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(player.OutputWaveFormat, Is.Null);
    }

    [Test]
    public void Constructor_OutsideBrowser_ThrowsPlatformNotSupportedException()
    {
        Assert.Throws<PlatformNotSupportedException>(() => new BrowserAudioWorkletPlayer());
    }

    [Test]
    public void Init_NullProvider_ThrowsArgumentNullException()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        Assert.Throws<ArgumentNullException>(() => player.Init(null));
    }

    [Test]
    public void Init_ValidProvider_SetsIeeeFloatOutputFormatMatchingSource()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        player.Init(Pcm16(44100, 2, 0, 0));

        Assert.That(player.OutputWaveFormat.Encoding, Is.EqualTo(WaveFormatEncoding.IeeeFloat));
        Assert.That(player.OutputWaveFormat.SampleRate, Is.EqualTo(44100));
        Assert.That(player.OutputWaveFormat.Channels, Is.EqualTo(2));
    }

    [Test]
    public void Init_CalledTwice_ThrowsInvalidOperationException()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());
        player.Init(Pcm16(48000, 1, 0));

        Assert.Throws<InvalidOperationException>(() => player.Init(Pcm16(48000, 1, 0)));
    }

    [Test]
    public void Init_MoreThanThirtyTwoChannels_ThrowsNotSupportedException()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        Assert.Throws<NotSupportedException>(
            () => player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 33), Array.Empty<short>())));
        Assert.That(player.OutputWaveFormat, Is.Null);
    }

    [Test]
    public void Play_BeforeInit_ThrowsInvalidOperationException()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        Assert.Throws<InvalidOperationException>(() => player.Play());
    }

    [Test]
    public void Play_AfterInit_StartsBridgeAndEntersPlayingState()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Init(Pcm16(48000, 2, 0, 0));

        player.Play();

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Playing));
        Assert.That(bridge.StartCount, Is.EqualTo(1));
        Assert.That(bridge.SampleRate, Is.EqualTo(48000));
        Assert.That(bridge.Channels, Is.EqualTo(2));
        Assert.That(bridge.BufferFrameCount, Is.EqualTo(12000));
    }

    [Test]
    public void Play_WhilePlaying_IsIdempotent()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Init(Pcm16(48000, 2, 0, 0));

        player.Play();
        player.Play();

        Assert.That(bridge.StartCount, Is.EqualTo(1));
    }

    [Test]
    public void PausePlay_TransitionsThroughPausedAndResumesBridge()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Init(Pcm16(48000, 2, 0, 0));
        player.Play();

        player.Pause();
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Paused));
        Assert.That(bridge.PauseCount, Is.EqualTo(1));

        player.Play();
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Playing));
        Assert.That(bridge.ResumeCount, Is.EqualTo(1));
        Assert.That(bridge.StartCount, Is.EqualTo(1), "resume must not re-start the graph");
    }

    [Test]
    public void Pause_WhenNotPlaying_IsNoOp()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Init(Pcm16(48000, 2, 0, 0));

        player.Pause();

        Assert.That(bridge.PauseCount, Is.EqualTo(0));
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
    }

    [TestCase(19)]
    [TestCase(5001)]
    public void Constructor_InvalidBufferDuration_ThrowsArgumentOutOfRangeException(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge(), milliseconds));
    }

    [TestCase(20, 8000, 512)]
    [TestCase(5000, 8000, 40000)]
    [TestCase(101, 44100, 4455)]
    public void Constructor_ValidBufferDuration_UsesExpectedFrameCapacity(
        int milliseconds,
        int sampleRate,
        int expectedFrames)
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge, milliseconds);
        player.Init(Pcm16(sampleRate, 1));

        player.Play();

        Assert.That(bridge.BufferFrameCount, Is.EqualTo(expectedFrames));
    }

    [Test]
    public void Constructor_CustomBufferDuration_IsConvertedToFrames()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge, 100);
        player.Init(Pcm16(48000, 2, 0, 0));

        player.Play();

        Assert.That(bridge.BufferFrameCount, Is.EqualTo(4800));
    }
}
