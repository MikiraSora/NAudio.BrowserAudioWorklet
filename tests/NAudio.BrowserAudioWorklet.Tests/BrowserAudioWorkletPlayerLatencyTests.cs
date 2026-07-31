using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.Browser;
using NUnit.Framework;

namespace NAudio.BrowserAudioWorklet.Tests;

[TestFixture]
[Category("UnitTest")]
public class BrowserAudioWorkletPlayerLatencyTests
{
    [Test]
    public void PrepareAsync_BeforeInit_ThrowsInvalidOperationException()
    {
        using var player = new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge());

        Assert.Throws<InvalidOperationException>(() => player.PrepareAsync());
    }

    [Test]
    public async Task PrepareAsync_AfterTransientFailure_CanBeRetried()
    {
        var failure = new InvalidOperationException("module unavailable");
        var bridge = new FakeAudioWorkletBridge { PrepareException = failure };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 1));

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() => player.PrepareAsync());
        bridge.PrepareException = null;
        BrowserAudioLatencyInfo latency = await player.PrepareAsync();

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(bridge.PrepareCount, Is.EqualTo(2));
            Assert.That(latency.SampleRate, Is.EqualTo(48000));
        });
    }

    [Test]
    public async Task PrepareAsync_IsIdempotentAndPublishesLatencyInfo()
    {
        var bridge = new FakeAudioWorkletBridge
        {
            BaseLatencySeconds = 0.004,
            OutputLatencySeconds = 0.012,
        };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 2, 0, 0));

        BrowserAudioLatencyInfo first = await player.PrepareAsync();
        BrowserAudioLatencyInfo second = await player.PrepareAsync();
        await player.PlayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.PrepareCount, Is.EqualTo(1));
            Assert.That(first, Is.SameAs(second));
            Assert.That(player.LatencyInfo, Is.SameAs(first));
            Assert.That(first.SampleRate, Is.EqualTo(48000));
            Assert.That(first.BaseLatencySeconds, Is.EqualTo(0.004));
            Assert.That(first.OutputLatencySeconds, Is.EqualTo(0.012));
            Assert.That(first.EstimatedDeviceLatencySeconds, Is.EqualTo(0.016).Within(0.000001));
        });
    }

    [TestCase(BrowserAudioLatencyProfile.Interactive, 960)]
    [TestCase(BrowserAudioLatencyProfile.Balanced, 3840)]
    [TestCase(BrowserAudioLatencyProfile.Playback, 12000)]
    public async Task LatencyProfile_UsesExpectedBufferAndInitialFrames(
        BrowserAudioLatencyProfile profile,
        int expectedBufferFrames)
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(
            bridge,
            BrowserAudioWorkletOptions.ForProfile(profile));
        player.Init(new TestSampleProvider(48000, 1));

        await player.PlayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.BufferFrameCount, Is.EqualTo(expectedBufferFrames));
            Assert.That(bridge.InitialBufferFrameCount, Is.EqualTo(512));
            Assert.That(bridge.UseDeviceSampleRate, Is.True);
        });
    }

    [TestCase(127)]
    [TestCase(8193)]
    public void Constructor_InvalidInitialBufferFrameCount_Throws(int frameCount)
    {
        var options = new BrowserAudioWorkletOptions
        {
            InitialBufferFrameCount = frameCount,
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge(), options));
    }

    [Test]
    public async Task CustomOptions_AreForwardedAndInitialTransferIsBoundedByTarget()
    {
        var bridge = new FakeAudioWorkletBridge();
        var options = new BrowserAudioWorkletOptions
        {
            BufferDurationMilliseconds = 20,
            InitialBufferFrameCount = 128,
            UseDeviceSampleRate = false,
        };
        using var player = new BrowserAudioWorkletPlayer(bridge, options);
        player.Init(new TestSampleProvider(8000, 1, 0));

        await player.PlayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.BufferFrameCount, Is.EqualTo(512));
            Assert.That(bridge.InitialBufferFrameCount, Is.EqualTo(128));
            Assert.That(bridge.UseDeviceSampleRate, Is.False);
            Assert.That(bridge.RequestLeadTimeSeconds, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public async Task InitSampleProvider_RendersDirectlyIntoBridgeBuffer()
    {
        var bridge = new FakeAudioWorkletBridge();
        var source = new TestSampleProvider(48000, 2, 0.25f, -0.5f);
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(source);
        await player.PlayAsync();
        var destination = new float[2];

        int frames = bridge.Render(destination, 1);

        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.EqualTo(1));
            Assert.That(source.LastDestination, Is.SameAs(destination));
            Assert.That(destination, Is.EqualTo(new[] { 0.25f, -0.5f }));
        });
    }

    [Test]
    public async Task PrepareAsync_DeviceRateUpdatesOutputFormatAndResamples()
    {
        var bridge = new FakeAudioWorkletBridge { PreparedSampleRate = 48000 };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(24000, 1, 0.25f, 0.5f, 0.75f, 1.0f));

        BrowserAudioLatencyInfo latency = await player.PrepareAsync();
        await player.PlayAsync();
        var destination = new float[32];
        int frames = bridge.Render(destination, destination.Length);

        Assert.Multiple(() =>
        {
            Assert.That(bridge.RequestedSampleRate, Is.EqualTo(24000));
            Assert.That(latency.SampleRate, Is.EqualTo(48000));
            Assert.That(player.OutputWaveFormat.SampleRate, Is.EqualTo(48000));
            Assert.That(frames, Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task FlushAsync_WhilePlayingFlushesBridgeWithoutRestart()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 1, 0));
        await player.PlayAsync();

        await player.FlushAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.StartCount, Is.EqualTo(1));
            Assert.That(bridge.FlushCount, Is.EqualTo(1));
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Playing));
        });
    }

    [Test]
    public async Task FlushAsync_WhileStopped_DoesNotStartOrFlushBridge()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 1, 0));

        await player.FlushAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.StartCount, Is.Zero);
            Assert.That(bridge.FlushCount, Is.Zero);
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        });
    }

    [Test]
    public async Task SeekAsync_SeekableProviderChangesPositionAndFlushes()
    {
        var bridge = new FakeAudioWorkletBridge();
        var source = new SeekableTestSampleProvider(48000, 1, TimeSpan.FromSeconds(10));
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(source);
        await player.PlayAsync();

        await player.SeekAsync(TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(source.Position, Is.EqualTo(TimeSpan.FromSeconds(3)));
            Assert.That(bridge.FlushCount, Is.EqualTo(1));
            Assert.That(bridge.StartCount, Is.EqualTo(1));
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Playing));
        });
    }

    [Test]
    public async Task SeekAsync_WhilePaused_PreservesPausedStateAndFlushes()
    {
        var bridge = new FakeAudioWorkletBridge();
        var source = new SeekableTestSampleProvider(48000, 1, TimeSpan.FromSeconds(10));
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(source);
        await player.PlayAsync();
        player.Pause();

        await player.SeekAsync(TimeSpan.FromSeconds(4));

        Assert.Multiple(() =>
        {
            Assert.That(source.Position, Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(bridge.FlushCount, Is.EqualTo(1));
            Assert.That(bridge.StartCount, Is.EqualTo(1));
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Paused));
        });
    }

    [Test]
    public async Task SeekAsync_WhileStopped_ClampsPositionWithoutFlushing()
    {
        var bridge = new FakeAudioWorkletBridge();
        var source = new SeekableTestSampleProvider(48000, 1, TimeSpan.FromSeconds(10));
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(source);

        await player.SeekAsync(TimeSpan.FromSeconds(12));

        Assert.Multiple(() =>
        {
            Assert.That(source.Position, Is.EqualTo(TimeSpan.FromSeconds(10)));
            Assert.That(bridge.FlushCount, Is.Zero);
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        });
    }

    [Test]
    public void SeekAsync_NegativePosition_ThrowsArgumentOutOfRangeException()
    {
        using var player = new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge());
        player.Init(new SeekableTestSampleProvider(48000, 1, TimeSpan.FromSeconds(10)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => player.SeekAsync(TimeSpan.FromMilliseconds(-1)));
    }

    [Test]
    public void SeekAsync_NonSeekableSource_ThrowsNotSupportedException()
    {
        using var player = new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge());
        player.Init(new TestSampleProvider(48000, 1, 0));

        Assert.Throws<NotSupportedException>(
            () => player.SeekAsync(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task FirstFrameEvent_CurrentRunPublishesEstimatedOutputTime()
    {
        var bridge = new FakeAudioWorkletBridge
        {
            BaseLatencySeconds = 0.005,
            OutputLatencySeconds = 0.01,
        };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        BrowserAudioFirstFrameEventArgs raised = null;
        player.FirstFrameRendered += (_, args) => raised = args;
        player.Init(new TestSampleProvider(48000, 1, 0));
        await player.PlayAsync();

        bridge.RaiseEvent(new AudioWorkletEvent("first-frame", 2.0, 0, 0.027));

        Assert.That(raised, Is.Not.Null);
        Assert.That(raised.ContextTimeSeconds, Is.EqualTo(2.0));
        Assert.That(raised.EstimatedOutputTimeSeconds, Is.EqualTo(2.015).Within(0.000001));
        Assert.That(raised.EstimatedStartToOutputLatencySeconds, Is.EqualTo(0.027));
    }

    [Test]
    public async Task UnderrunEvent_CurrentRunReportsMissingFrames()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        BrowserAudioUnderrunEventArgs raised = null;
        player.BufferUnderrun += (_, args) => raised = args;
        player.Init(new TestSampleProvider(48000, 1, 0));
        await player.PlayAsync();

        bridge.RaiseEvent(new AudioWorkletEvent("underrun", 0, 384));

        Assert.That(raised, Is.Not.Null);
        Assert.That(raised.MissingFrames, Is.EqualTo(384));
    }

    [Test]
    public async Task DiagnosticFromPreviousRun_DoesNotReachCurrentRun()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        var firstFrames = new List<double>();
        player.FirstFrameRendered += (_, args) => firstFrames.Add(args.ContextTimeSeconds);
        player.Init(new TestSampleProvider(48000, 1, 0, 0));
        await player.PlayAsync();
        player.Stop();
        await player.PlayAsync();

        bridge.RaiseEventForRun(0, new AudioWorkletEvent("first-frame", 1.0, 0));
        bridge.RaiseEventForRun(1, new AudioWorkletEvent("first-frame", 2.0, 0));

        Assert.That(firstFrames, Is.EqualTo(new[] { 2.0 }));
    }

    [Test]
    public async Task GetPlaybackMetricsAsync_ForwardsBridgeMetrics()
    {
        var expected = new BrowserAudioPlaybackMetrics(2, 640, 1.25, true, 0.031);
        var bridge = new FakeAudioWorkletBridge { Metrics = expected };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 1, 0));
        await player.PlayAsync();

        BrowserAudioPlaybackMetrics actual = await player.GetPlaybackMetricsAsync();

        Assert.That(actual, Is.SameAs(expected));
    }

    [Test]
    public async Task GetPlaybackMetricsAsync_BeforePreparation_ReturnsEmptyMetrics()
    {
        using var player = new BrowserAudioWorkletPlayer(new FakeAudioWorkletBridge());
        player.Init(new TestSampleProvider(48000, 1, 0));

        BrowserAudioPlaybackMetrics metrics = await player.GetPlaybackMetricsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(metrics.UnderrunCount, Is.Zero);
            Assert.That(metrics.UnderrunFrameCount, Is.Zero);
            Assert.That(metrics.FirstFrameContextTimeSeconds, Is.Null);
            Assert.That(metrics.IsFirstFrameRendered, Is.False);
            Assert.That(metrics.EstimatedStartToOutputLatencySeconds, Is.Null);
        });
    }

    [Test]
    public async Task StopThenPlay_ReusesPreparationButStartsANewRun()
    {
        var bridge = new FakeAudioWorkletBridge();
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 1, 0, 0));
        await player.PlayAsync();
        player.Stop();

        await player.PlayAsync();

        Assert.Multiple(() =>
        {
            Assert.That(bridge.PrepareCount, Is.EqualTo(1));
            Assert.That(bridge.StartCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task StopDuringPreparation_DoesNotStartAStaleRun()
    {
        var preparation = new TaskCompletionSource<AudioWorkletPreparation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new FakeAudioWorkletBridge { PrepareCompletion = preparation };
        using var player = new BrowserAudioWorkletPlayer(bridge);
        player.Init(new TestSampleProvider(48000, 1, 0));

        Task playTask = player.PlayAsync();
        player.Stop();
        preparation.SetResult(new AudioWorkletPreparation(48000, 0.005, 0.01));
        await playTask;

        Assert.Multiple(() =>
        {
            Assert.That(bridge.StartCount, Is.Zero);
            Assert.That(bridge.StopCount, Is.EqualTo(1));
            Assert.That(player.PlaybackState, Is.EqualTo(PlaybackState.Stopped));
        });
    }
}
