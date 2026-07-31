using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.Browser;
using NUnit.Framework;

namespace NAudio.BrowserAudioWorklet.Tests;

[TestFixture]
[Category("UnitTest")]
public sealed class LatencyMeasureHelperTests
{
    [Test]
    public void MeasureLatency_PublicApi_UsesBrowserOptionsAndHasNoAliasOrPublicTimingProperty()
    {
        var method = typeof(LatencyMeasureHelper).GetMethod(nameof(LatencyMeasureHelper.MeasureLatency));
        var firstFrameType = typeof(BrowserAudioFirstFrameEventArgs);

        Assert.Multiple(() =>
        {
            Assert.That(method, Is.Not.Null);
            Assert.That(method.IsPublic, Is.True);
            Assert.That(method.IsStatic, Is.True);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(Task<TimeSpan>)));
            Assert.That(method.GetParameters(), Has.Length.EqualTo(1));
            Assert.That(method.GetParameters()[0].ParameterType,
                Is.EqualTo(typeof(BrowserAudioWorkletOptions)));
            Assert.That(typeof(LatencyMeasureHelper).Assembly.GetType(
                "NAudio.Wave.Browser.AudioWorkletOptions"), Is.Null);
            Assert.That(firstFrameType.GetProperty(
                "ObservedResumeToFirstFrameLatency",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance), Is.Null);
        });
    }

    [Test]
    public async Task MeasureLatency_UsesZeroOutputGainBeforeEveryRunWhileRenderingNonZeroProbeFrames()
    {
        var bridge = new LatencyMeasureBridge(1, 2, 3, 4, 5, 6);
        var options = new BrowserAudioWorkletOptions
        {
            BufferDurationMilliseconds = 20,
            InitialBufferFrameCount = 128,
            UseDeviceSampleRate = false,
        };

        TimeSpan measured = await LatencyMeasureHelper.MeasureLatency(
            options,
            bridge,
            TimeSpan.FromSeconds(1));

        float expectedSecondSample = (float)(0.2 * Math.Sin(2 * Math.PI * 440 / 48000));
        Assert.Multiple(() =>
        {
            Assert.That(measured, Is.EqualTo(TimeSpan.FromMilliseconds(4)));
            Assert.That(bridge.PrepareCount, Is.EqualTo(1));
            Assert.That(bridge.StartCount, Is.EqualTo(6));
            Assert.That(bridge.DisposeCount, Is.EqualTo(1));
            Assert.That(bridge.RequestedSampleRate, Is.EqualTo(48000));
            Assert.That(bridge.Channels, Is.EqualTo(2));
            Assert.That(bridge.UseDeviceSampleRate, Is.False);
            Assert.That(bridge.InitialBufferFrameCount, Is.EqualTo(128));
            Assert.That(bridge.VolumeChanges, Is.Not.Empty);
            Assert.That(bridge.VolumeChanges, Is.All.EqualTo(0.0f));
            Assert.That(bridge.VolumeAtStart, Has.Count.EqualTo(6));
            Assert.That(bridge.VolumeAtStart, Is.All.EqualTo(0.0f));
            Assert.That(bridge.RenderedRuns, Has.Count.EqualTo(6));
            Assert.That(bridge.RenderedRuns[0], Has.Length.EqualTo(4800 * 2));
            Assert.That(bridge.RenderedRuns[0][0], Is.EqualTo(0).Within(0.000001));
            Assert.That(bridge.RenderedRuns[0][1], Is.EqualTo(0).Within(0.000001));
            Assert.That(expectedSecondSample, Is.GreaterThan(0));
            Assert.That(bridge.RenderedRuns[0][2], Is.EqualTo(expectedSecondSample).Within(0.000001));
            Assert.That(bridge.RenderedRuns[0][3], Is.EqualTo(expectedSecondSample).Within(0.000001));
            for (int run = 1; run < bridge.RenderedRuns.Count; run++)
            {
                Assert.That(bridge.RenderedRuns[run][0], Is.EqualTo(bridge.RenderedRuns[0][0]));
                Assert.That(bridge.RenderedRuns[run][2], Is.EqualTo(bridge.RenderedRuns[0][2]));
            }
        });
    }

    [Test]
    public void MeasureLatency_PropagatesBrowserStartFailureAndReleasesPlayer()
    {
        var failure = new BrowserAudioException("autoplay denied");
        var bridge = new LatencyMeasureBridge(1, 2, 3, 4, 5, 6)
        {
            StartException = failure,
        };

        var thrown = Assert.ThrowsAsync<BrowserAudioException>(() =>
            LatencyMeasureHelper.MeasureLatency(
                BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Interactive),
                bridge,
                TimeSpan.FromSeconds(1)));

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(bridge.PrepareCount, Is.EqualTo(1));
            Assert.That(bridge.StartCount, Is.EqualTo(1));
            Assert.That(bridge.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MeasureLatency_PropagatesPreparationFailureAndReleasesPlayer()
    {
        var failure = new InvalidOperationException("worklet module unavailable");
        var bridge = new LatencyMeasureBridge(1, 2, 3, 4, 5, 6)
        {
            PrepareException = failure,
        };

        var thrown = Assert.ThrowsAsync<InvalidOperationException>(() =>
            LatencyMeasureHelper.MeasureLatency(
                BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Interactive),
                bridge,
                TimeSpan.FromSeconds(1)));

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(bridge.PrepareCount, Is.EqualTo(1));
            Assert.That(bridge.StartCount, Is.Zero);
            Assert.That(bridge.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MeasureLatency_PropagatesNaturalStopFailureAndReleasesPlayer()
    {
        var failure = new BrowserAudioException("AudioWorklet stopped unexpectedly");
        var bridge = new LatencyMeasureBridge(1)
        {
            StopException = failure,
        };

        var thrown = Assert.ThrowsAsync<BrowserAudioException>(() =>
            LatencyMeasureHelper.MeasureLatency(
                BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Interactive),
                bridge,
                TimeSpan.FromSeconds(1)));

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(bridge.StartCount, Is.EqualTo(1));
            Assert.That(bridge.DisposeCount, Is.EqualTo(1));
        });
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void MeasureLatency_WhenRequiredEventIsMissing_ThrowsTimeoutAndReleasesPlayer(
        bool emitFirstFrame,
        bool emitStopped)
    {
        var bridge = new LatencyMeasureBridge(1)
        {
            EmitFirstFrame = emitFirstFrame,
            EmitStopped = emitStopped,
        };

        var thrown = Assert.ThrowsAsync<TimeoutException>(() =>
            LatencyMeasureHelper.MeasureLatency(
                BrowserAudioWorkletOptions.ForProfile(BrowserAudioLatencyProfile.Interactive),
                bridge,
                TimeSpan.FromMilliseconds(10)));

        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.Not.Null);
            Assert.That(bridge.StartCount, Is.EqualTo(1));
            Assert.That(bridge.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MeasureLatency_NullOptions_ThrowsArgumentNullExceptionBeforePlatformCheck()
    {
        var thrown = Assert.Throws<ArgumentNullException>(() => LatencyMeasureHelper.MeasureLatency(null));

        Assert.That(thrown.ParamName, Is.EqualTo("options"));
    }

    [TestCase(19, 512)]
    [TestCase(5001, 512)]
    [TestCase(20, 127)]
    [TestCase(20, 8193)]
    public void MeasureLatency_InvalidOptions_UsesPlayerValidation(
        int bufferDurationMilliseconds,
        int initialBufferFrameCount)
    {
        var options = new BrowserAudioWorkletOptions
        {
            BufferDurationMilliseconds = bufferDurationMilliseconds,
            InitialBufferFrameCount = initialBufferFrameCount,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LatencyMeasureHelper.MeasureLatency(options, new LatencyMeasureBridge(1), TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void MeasureLatency_OnNonBrowserTarget_ThrowsPlatformNotSupportedException()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            LatencyMeasureHelper.MeasureLatency(BrowserAudioWorkletOptions.ForProfile(
                BrowserAudioLatencyProfile.Interactive)));
    }

    private sealed class LatencyMeasureBridge : IAudioWorkletBridge
    {
        private readonly Queue<double> firstFrameLatencies;
        private float currentVolume = 1.0f;

        public LatencyMeasureBridge(params double[] firstFrameLatencies)
        {
            this.firstFrameLatencies = new Queue<double>(firstFrameLatencies);
        }

        public int PrepareCount { get; private set; }
        public int StartCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int RequestedSampleRate { get; private set; }
        public int Channels { get; private set; }
        public int InitialBufferFrameCount { get; private set; }
        public bool UseDeviceSampleRate { get; private set; }
        public Exception PrepareException { get; set; }
        public Exception StartException { get; set; }
        public Exception StopException { get; set; }
        public bool EmitFirstFrame { get; set; } = true;
        public bool EmitStopped { get; set; } = true;
        public List<float> VolumeChanges { get; } = new();
        public List<float> VolumeAtStart { get; } = new();
        public List<float[]> RenderedRuns { get; } = new();

        public Task<AudioWorkletPreparation> PrepareAsync(
            int requestedSampleRate,
            int channels,
            bool useDeviceSampleRate)
        {
            PrepareCount++;
            RequestedSampleRate = requestedSampleRate;
            Channels = channels;
            UseDeviceSampleRate = useDeviceSampleRate;
            if (PrepareException != null)
            {
                return Task.FromException<AudioWorkletPreparation>(PrepareException);
            }

            return Task.FromResult(new AudioWorkletPreparation(
                requestedSampleRate,
                0.001,
                0.002));
        }

        public Task StartAsync(
            int channels,
            int bufferFrameCount,
            int initialBufferFrameCount,
            double requestLeadTimeSeconds,
            AudioRenderCallback renderFrames,
            Action<Exception> onStopped,
            Action<AudioWorkletEvent> onEvent)
        {
            StartCount++;
            Channels = channels;
            InitialBufferFrameCount = initialBufferFrameCount;
            VolumeAtStart.Add(currentVolume);
            if (StartException != null)
            {
                return Task.FromException(StartException);
            }

            var rendered = new List<float>();
            while (true)
            {
                var destination = new float[channels * 257];
                int frames = renderFrames(destination, 257);
                if (frames <= 0)
                {
                    break;
                }

                int sampleCount = frames * channels;
                rendered.AddRange(destination.AsSpan(0, sampleCount).ToArray());
            }

            RenderedRuns.Add(rendered.ToArray());
            if (EmitFirstFrame)
            {
                double latency = firstFrameLatencies.Count == 0 ? 0 : firstFrameLatencies.Dequeue();
                onEvent(new AudioWorkletEvent("first-frame", 0, 0, 0.01, latency));
            }

            if (EmitStopped)
            {
                onStopped(StopException);
            }

            return Task.CompletedTask;
        }

        public Task FlushAsync() => Task.CompletedTask;

        public Task PauseAsync() => Task.CompletedTask;

        public Task ResumeAsync() => Task.CompletedTask;

        public void SetVolume(float volume)
        {
            currentVolume = volume;
            VolumeChanges.Add(volume);
        }

        public Task StopAsync() => Task.CompletedTask;

        public long TotalConsumedFrameCount => 0;

        public Task ResetTotalConsumedAsync() => Task.CompletedTask;

        public Task<BrowserAudioPlaybackMetrics> GetMetricsAsync()
            => Task.FromResult(new BrowserAudioPlaybackMetrics(0, 0, null, false));

        public void Dispose() => DisposeCount++;

        public Task DisposeAsync()
        {
            Dispose();
            return Task.CompletedTask;
        }
    }
}
