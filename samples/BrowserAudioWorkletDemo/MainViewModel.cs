using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using NAudio.Wave;
using NAudio.Wave.Browser;
using NAudio.Wave.SampleProviders;

namespace BrowserAudioWorkletDemo;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BrowserAudioWorkletPlayer player;
    private readonly SignalGenerator signal;
    private readonly AsyncCommand playCommand;
    private readonly DelegateCommand pauseCommand;
    private readonly AsyncCommand stopCommand;
    private readonly AsyncCommand resetConsumedCommand;
    private readonly DispatcherTimer consumedTimer;
    private PlaybackState playbackState;
    private string status = "Ready";
    private double frequency = 440;
    private double volume = 0.7;
    private long totalConsumedFrameCount;
    private long totalConsumedSampleCount;
    private TimeSpan totalConsumedTime;
    private bool disposed;

    public MainViewModel()
    {
        signal = new SignalGenerator(48_000, 2)
        {
            Frequency = frequency,
            Gain = 0.2,
            Type = SignalGeneratorType.Sin
        };

        player = new BrowserAudioWorkletPlayer(BrowserAudioLatencyProfile.Interactive);
        player.Init(signal);
        player.Volume = (float)volume;
        player.PlaybackStopped += OnPlaybackStopped;
        _ = PreparePlayerAsync();

        playCommand = new AsyncCommand(PlayAsync, () => playbackState != PlaybackState.Playing);
        pauseCommand = new DelegateCommand(Pause, () => playbackState == PlaybackState.Playing);
        stopCommand = new AsyncCommand(StopAsync, () => playbackState != PlaybackState.Stopped);
        resetConsumedCommand = new AsyncCommand(
            ResetConsumedAsync,
            () => !disposed);
        consumedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        consumedTimer.Tick += OnConsumedTimerTick;
        consumedTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand PlayCommand => playCommand;

    public ICommand PauseCommand => pauseCommand;

    public ICommand StopCommand => stopCommand;

    public ICommand ResetConsumedCommand => resetConsumedCommand;

    public string Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public double Frequency
    {
        get => frequency;
        set
        {
            if (SetField(ref frequency, value))
            {
                signal.Frequency = value;
            }
        }
    }

    public double Volume
    {
        get => volume;
        set
        {
            if (SetField(ref volume, value))
            {
                player.Volume = (float)value;
            }
        }
    }

    public long TotalConsumedFrameCount
    {
        get => totalConsumedFrameCount;
        private set => SetField(ref totalConsumedFrameCount, value);
    }

    public long TotalConsumedSampleCount
    {
        get => totalConsumedSampleCount;
        private set => SetField(ref totalConsumedSampleCount, value);
    }

    public string TotalConsumedTimeText
        => totalConsumedTime.ToString(@"hh\:mm\:ss\.fff");

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        player.PlaybackStopped -= OnPlaybackStopped;
        consumedTimer.Stop();
        consumedTimer.Tick -= OnConsumedTimerTick;
        player.Dispose();
    }

    private async Task PlayAsync()
    {
        try
        {
            if (playbackState != PlaybackState.Paused)
            {
                await player.ResetTotalConsumedAsync();
            }

            await player.PlayAsync();
            SetPlaybackState(PlaybackState.Playing, "Playing");
            RefreshConsumed();
        }
        catch (Exception ex)
        {
            SetPlaybackState(PlaybackState.Stopped, $"Playback failed: {RootMessage(ex)}");
        }
    }

    private async Task PreparePlayerAsync()
    {
        try
        {
            await player.PrepareAsync();
        }
        catch
        {
            // PlayAsync retries preparation and reports a foreground error if it still fails.
        }
    }

    private void Pause()
    {
        player.Pause();
        SetPlaybackState(PlaybackState.Paused, "Paused");
    }

    private async Task StopAsync()
    {
        player.Stop();
        try
        {
            await player.ResetTotalConsumedAsync();
            SetPlaybackState(PlaybackState.Stopped, "Stopped");
            RefreshConsumed();
        }
        catch (Exception ex)
        {
            SetPlaybackState(
                PlaybackState.Stopped,
                $"Stopped; unable to reset consumed counter: {RootMessage(ex)}");
        }
    }

    private async Task ResetConsumedAsync()
    {
        try
        {
            await player.ResetTotalConsumedAsync();
            RefreshConsumed();
            Status = "Consumed counter reset";
        }
        catch (Exception ex)
        {
            Status = $"Unable to reset consumed counter: {RootMessage(ex)}";
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        RefreshConsumed();
        SetPlaybackState(
            PlaybackState.Stopped,
            e.Exception is null ? "Stopped" : $"Playback failed: {e.Exception.Message}");
    }

    private void OnConsumedTimerTick(object? sender, EventArgs e)
        => RefreshConsumed();

    private void RefreshConsumed()
    {
        if (disposed)
        {
            return;
        }

        TotalConsumedFrameCount = player.TotalConsumedFrameCount;
        TotalConsumedSampleCount = player.TotalConsumedSampleCount;
        TimeSpan nextTime = player.TotalConsumedTime;
        if (nextTime != totalConsumedTime)
        {
            totalConsumedTime = nextTime;
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(TotalConsumedTimeText)));
        }
    }

    private void SetPlaybackState(PlaybackState value, string message)
    {
        playbackState = value;
        Status = message;
        playCommand.RaiseCanExecuteChanged();
        pauseCommand.RaiseCanExecuteChanged();
        stopCommand.RaiseCanExecuteChanged();
    }

    private static string RootMessage(Exception error)
    {
        while (error.InnerException != null)
        {
            error = error.InnerException;
        }

        return error.Message;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private sealed class DelegateCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        private bool executing;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !executing && canExecute();

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            executing = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            finally
            {
                executing = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
