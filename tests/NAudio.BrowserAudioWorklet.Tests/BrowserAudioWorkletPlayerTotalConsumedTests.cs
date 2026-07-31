using System;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.Browser;
using NUnit.Framework;

namespace NAudio.BrowserAudioWorklet.Tests;

[TestFixture]
[Category("UnitTest")]
public class BrowserAudioWorkletPlayerTotalConsumedTests
{
    [Test]
    public void TotalConsumedProperties_NewUninitialisedPlayer_ReturnZero()
    {
        using var player = new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge());

        Assert.Multiple(() =>
        {
            Assert.That(player.TotalConsumedFrameCount, Is.Zero);
            Assert.That(player.TotalConsumedSampleCount, Is.Zero);
            Assert.That(player.TotalConsumedTime, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task TotalConsumedProperties_StereoOutput_UseFramesChannelsAndOutputSampleRate()
    {
        const long frameCount = 96_001;
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48_000, 2, 0, 0));
        await player.PlayAsync();
        bridge.SetTotalConsumedFrameCount(frameCount);

        Assert.Multiple(() =>
        {
            Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(frameCount));
            Assert.That(player.TotalConsumedSampleCount, Is.EqualTo(frameCount * 2));
            Assert.That(
                player.TotalConsumedTime,
                Is.EqualTo(TimeSpan.FromSeconds(frameCount / 48_000d)));
        });
    }

    [Test]
    public async Task TotalConsumedTime_DeviceSampleRate_UsesPreparedOutputRate()
    {
        var bridge = new FakeAudioWorkletBridge { PreparedSampleRate = 48_000 };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(24_000, 1, 0));
        await player.PrepareAsync();
        bridge.SetTotalConsumedFrameCount(24_000);

        Assert.Multiple(() =>
        {
            Assert.That(player.OutputWaveFormat.SampleRate, Is.EqualTo(48_000));
            Assert.That(player.TotalConsumedTime, Is.EqualTo(TimeSpan.FromSeconds(0.5)));
        });
    }

    [Test]
    public async Task TotalConsumedProperties_RepeatedReads_ReturnExactStable64BitValues()
    {
        const long expectedFrames = (long)uint.MaxValue + 513;
        const int channels = 2;
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48_000, channels, 0, 0));
        await player.PlayAsync();
        bridge.SetTotalConsumedFrameCount(expectedFrames);

        for (int index = 0; index < 4096; index++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames));
                Assert.That(player.TotalConsumedSampleCount, Is.EqualTo(expectedFrames * channels));
                Assert.That(
                    player.TotalConsumedTime,
                    Is.EqualTo(TimeSpan.FromSeconds(expectedFrames / 48_000d)));
            });
        }
    }

    [Test]
    public async Task ResetTotalConsumedAsync_ExplicitReset_ClearsAllTotalsExactlyOnce()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48_000, 2, 0, 0));
        await player.PlayAsync();
        bridge.SetTotalConsumedFrameCount(4097);

        await player.ResetTotalConsumedAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.ResetTotalConsumedCount, Is.EqualTo(1));
            Assert.That(player.TotalConsumedFrameCount, Is.Zero);
            Assert.That(player.TotalConsumedSampleCount, Is.Zero);
            Assert.That(player.TotalConsumedTime, Is.EqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task LifecycleOperations_DoNotResetTotalConsumedProgress()
    {
        const long expectedFrames = 12_345;
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        var source = new SeekableTestSampleProvider(
            48_000,
            1,
            TimeSpan.FromSeconds(10));
        player.Init(source);
        await player.PlayAsync();
        bridge.SetTotalConsumedFrameCount(expectedFrames);

        player.Pause();
        Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "pause");

        await player.PlayAsync();
        Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "resume/play");

        await player.FlushAsync();
        Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "flush");

        await player.SeekAsync(TimeSpan.FromSeconds(3));
        Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "seek");

        player.Stop();
        Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "stop");

        await player.PlayAsync();
        Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "new play run");

        bridge.RaiseStopped();
        Assert.Multiple(() =>
        {
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
            Assert.That(player.TotalConsumedFrameCount, Is.EqualTo(expectedFrames), "natural end");
            Assert.That(bridge.ResetTotalConsumedCount, Is.Zero, "only the explicit reset API may clear totals");
        });
    }

    [Test]
    public void ResetTotalConsumedAsync_BridgeFailure_PropagatesOriginalException()
    {
        var failure = new InvalidOperationException("reset failed");
        var bridge = new FakeAudioWorkletBridge { ResetTotalConsumedException = failure };
        using var player = new BrowserAudioWorkletPlayer(bridge);

        InvalidOperationException thrown = Assert.ThrowsAsync<InvalidOperationException>(
            () => player.ResetTotalConsumedAsync());

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(bridge.ResetTotalConsumedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void TotalConsumedApis_AfterDispose_ThrowObjectDisposedException()
    {
        var player = new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge());
        player.Dispose();

        Assert.Multiple(() =>
        {
            Assert.Throws<ObjectDisposedException>(() => _ = player.TotalConsumedFrameCount);
            Assert.Throws<ObjectDisposedException>(() => _ = player.TotalConsumedSampleCount);
            Assert.Throws<ObjectDisposedException>(() => _ = player.TotalConsumedTime);
            Assert.ThrowsAsync<ObjectDisposedException>(() => player.ResetTotalConsumedAsync());
        });
    }
}
