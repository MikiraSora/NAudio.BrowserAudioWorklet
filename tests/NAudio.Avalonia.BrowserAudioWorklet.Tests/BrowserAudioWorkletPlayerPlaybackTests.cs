using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.Browser;
using NUnit.Framework;

namespace NAudio.Avalonia.BrowserAudioWorklet.Tests;

[TestFixture]
[Category("UnitTest")]
public class BrowserAudioWorkletPlayerPlaybackTests
{
    private static BrowserAudioWorkletPlayer CreatePlayer(FakeAudioWorkletBridge bridge)
        => new(bridge);

    private static float ReadFloat(ReadOnlySpan<byte> bytes, int index)
        => MemoryMarshal.Read<float>(bytes.Slice(index * sizeof(float), sizeof(float)));

    [Test]
    public void Render_ConvertsPcm16ToNormalisedFloatPreservingChannelOrder()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        // Stereo: L=full-scale positive, R=full-scale negative, then a second frame.
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2),
            new short[] { short.MaxValue, short.MinValue, 0, short.MaxValue }));
        player.Play();

        var destination = new byte[4 * sizeof(float)];
        int frames = bridge.Render(destination, frameCount: 2);

        Assert.That(frames, Is.EqualTo(2));
        Assert.That(ReadFloat(destination, 0), Is.EqualTo(1.0f).Within(0.001f), "frame0 L");
        Assert.That(ReadFloat(destination, 1), Is.EqualTo(-1.0f).Within(0.001f), "frame0 R");
        Assert.That(ReadFloat(destination, 2), Is.EqualTo(0.0f).Within(0.001f), "frame1 L");
        Assert.That(ReadFloat(destination, 3), Is.EqualTo(1.0f).Within(0.001f), "frame1 R");
    }

    [Test]
    public void Render_PartialFinalRead_ReturnsOnlyFramesActuallyProduced()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        // Only one stereo frame available, but the worklet asks for four.
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2),
            new short[] { 100, 200 }));
        player.Play();

        var destination = new byte[4 * 2 * sizeof(float)];
        int frames = bridge.Render(destination, frameCount: 4);

        Assert.That(frames, Is.EqualTo(1));
    }

    [Test]
    public void Render_EndOfStream_ReturnsZeroFrames()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), Array.Empty<short>()));
        player.Play();

        int frames = bridge.Render(new byte[16], frameCount: 4);

        Assert.That(frames, Is.EqualTo(0));
    }

    [Test]
    public void Render_SourceReturnsPartialFrame_ThrowsInvalidOperationException()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 100 }));
        player.Play();

        Assert.Throws<InvalidOperationException>(
            () => bridge.Render(new byte[2 * sizeof(float)], frameCount: 1));
    }

    [Test]
    public void BridgeStopped_NoError_TransitionsToStoppedAndRaisesPlaybackStoppedOnceWithoutException()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));
        player.Play();

        bridge.RaiseStopped(null);

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(raised, Has.Count.EqualTo(1));
        Assert.That(raised[0].Exception, Is.Null);
    }

    [Test]
    public void BridgeStopped_WithError_RaisesPlaybackStoppedOnceCarryingOriginalException()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));
        player.Play();
        var failure = new BrowserAudioException("worklet failed");

        bridge.RaiseStopped(failure);

        Assert.That(raised, Has.Count.EqualTo(1));
        Assert.That(raised[0].Exception, Is.SameAs(failure));
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public void Stop_WhilePlaying_StopsBridgeAndRaisesPlaybackStoppedWithoutException()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));
        player.Play();

        player.Stop();

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(bridge.StopCount, Is.EqualTo(1));
        Assert.That(raised, Has.Count.EqualTo(1));
        Assert.That(raised[0].Exception, Is.Null);
    }

    [Test]
    public void Stop_ThenBridgeStopped_RaisesPlaybackStoppedOnlyOnce()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));
        player.Play();

        player.Stop();
        bridge.RaiseStopped(null);

        Assert.That(raised, Has.Count.EqualTo(1), "explicit stop and a late graph-stop must not double-raise");
    }

    [Test]
    public void LateCallbackFromPreviousRun_DoesNotStopCurrentRun()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), new short[] { 0 }));

        player.Play();
        player.Stop();
        player.Play();
        bridge.RaiseStoppedForRun(0, new BrowserAudioException("late failure"));

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Playing));
        Assert.That(raised, Has.Count.EqualTo(1), "the first run's explicit stop is the only event so far");

        bridge.RaiseStoppedForRun(1);

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(raised, Has.Count.EqualTo(2));
        Assert.That(raised[1].Exception, Is.Null);
    }

    [Test]
    public void Stop_WhenStopped_IsNoOp()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));

        player.Stop();

        Assert.That(bridge.StopCount, Is.EqualTo(0));
        Assert.That(raised, Is.Empty);
    }

    [Test]
    public void Stop_WhenBridgeTeardownFaults_StillReportsSuccessfulExplicitStop()
    {
        var bridge = new FakeAudioWorkletBridge { StopException = new InvalidOperationException("close failed") };
        using var player = CreatePlayer(bridge);
        StoppedEventArgs stopped = null;
        player.PlaybackStopped += (_, e) => stopped = e;
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), new short[] { 0 }));
        player.Play();

        player.Stop();

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(stopped, Is.Not.Null);
        Assert.That(stopped.Exception, Is.Null);
    }

    [Test]
    public void Play_WhenBridgeStartFaults_ReportsFailureThroughPlaybackStopped()
    {
        var bridge = new FakeAudioWorkletBridge { StartException = new BrowserAudioException("no context") };
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));

        player.Play();

        Assert.That(raised, Has.Count.EqualTo(1));
        Assert.That(raised[0].Exception, Is.TypeOf<BrowserAudioException>());
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public void Volume_DefaultsToUnity()
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        Assert.That(player.Volume, Is.EqualTo(1.0f));
    }

    [Test]
    public void Volume_SetBeforeInit_IsForwardedToBridgeAndAppliedAfterInit()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);

        player.Volume = 0.5f;

        Assert.That(bridge.LastVolume, Is.EqualTo(0.5f));
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));
        Assert.That(player.Volume, Is.EqualTo(0.5f));
    }

    [Test]
    public void Volume_IsAppliedOnlyByBridge_NotTwiceToRenderedSamples()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);
        player.Volume = 0.5f;
        player.Init(new SequenceWaveProvider(
            new WaveFormat(48000, 16, 1),
            new short[] { short.MaxValue }));
        player.Play();

        var destination = new byte[sizeof(float)];
        bridge.Render(destination, frameCount: 1);

        Assert.That(ReadFloat(destination, 0), Is.EqualTo(1.0f).Within(0.001f));
        Assert.That(bridge.LastVolume, Is.EqualTo(0.5f));
    }

    [TestCase(-0.01f)]
    [TestCase(1.01f)]
    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    public void Volume_InvalidValue_ThrowsArgumentOutOfRangeException(float value)
    {
        using var player = CreatePlayer(new FakeAudioWorkletBridge());

        Assert.Throws<ArgumentOutOfRangeException>(() => player.Volume = value);
    }

    [TestCase(0.0f)]
    [TestCase(1.0f)]
    public void Volume_BoundaryValue_IsAcceptedAndForwarded(float value)
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = CreatePlayer(bridge);

        player.Volume = value;

        Assert.That(player.Volume, Is.EqualTo(value));
        Assert.That(bridge.LastVolume, Is.EqualTo(value));
    }

    [Test]
    public void Pause_WhenBridgeFaults_StopsAndReportsWrappedFailure()
    {
        var original = new InvalidOperationException("suspend failed");
        var bridge = new FakeAudioWorkletBridge { PauseException = original };
        using var player = CreatePlayer(bridge);
        StoppedEventArgs stopped = null;
        player.PlaybackStopped += (_, e) => stopped = e;
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), new short[] { 0 }));
        player.Play();

        player.Pause();

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(stopped, Is.Not.Null);
        Assert.That(stopped.Exception, Is.TypeOf<BrowserAudioException>());
        Assert.That(stopped.Exception.InnerException, Is.SameAs(original));
    }

    [Test]
    public void PlayAsync_WhenResumeFaults_PropagatesAndReportsWrappedFailure()
    {
        var original = new InvalidOperationException("resume failed");
        var bridge = new FakeAudioWorkletBridge { ResumeException = original };
        using var player = CreatePlayer(bridge);
        StoppedEventArgs stopped = null;
        player.PlaybackStopped += (_, e) => stopped = e;
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), new short[] { 0 }));
        player.Play();
        player.Pause();

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => player.PlayAsync());

        Assert.That(thrown, Is.SameAs(original));
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(stopped, Is.Not.Null);
        Assert.That(stopped.Exception, Is.TypeOf<BrowserAudioException>());
        Assert.That(stopped.Exception.InnerException, Is.SameAs(original));
    }

    [Test]
    public void PlayAsync_WhenBridgeStartFaults_PropagatesAndReportsWrappedFailure()
    {
        var original = new InvalidOperationException("no audio context");
        var bridge = new FakeAudioWorkletBridge { StartException = original };
        using var player = CreatePlayer(bridge);
        StoppedEventArgs stopped = null;
        player.PlaybackStopped += (_, e) => stopped = e;
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), new short[] { 0 }));

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => player.PlayAsync());

        Assert.That(thrown, Is.SameAs(original));
        Assert.That(stopped, Is.Not.Null);
        Assert.That(stopped.Exception, Is.TypeOf<BrowserAudioException>());
        Assert.That(stopped.Exception.InnerException, Is.SameAs(original));
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public async Task Stop_DuringPendingStart_RemainsStoppedAndRaisesOnce()
    {
        var startCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeAudioWorkletBridge { StartCompletion = startCompletion };
        using var player = CreatePlayer(bridge);
        var raised = new List<StoppedEventArgs>();
        player.PlaybackStopped += (_, e) => raised.Add(e);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 1), new short[] { 0 }));

        Task playTask = player.PlayAsync();
        player.Stop();
        startCompletion.SetResult();
        await playTask;

        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(bridge.StopCount, Is.EqualTo(1));
        Assert.That(raised, Has.Count.EqualTo(1));
        Assert.That(raised[0].Exception, Is.Null);
    }

    [Test]
    public void Dispose_DisposesBridgeAndIsIdempotent()
    {
        var bridge = new FakeAudioWorkletBridge();
        var player = CreatePlayer(bridge);

        player.Dispose();
        player.Dispose();

        Assert.That(bridge.DisposeCount, Is.EqualTo(1));
        Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public void Play_AfterDispose_ThrowsObjectDisposedException()
    {
        var bridge = new FakeAudioWorkletBridge();
        var player = CreatePlayer(bridge);
        player.Init(new SequenceWaveProvider(new WaveFormat(48000, 16, 2), new short[] { 0, 0 }));
        player.Dispose();

        Assert.Throws<ObjectDisposedException>(() => player.Play());
    }
}
