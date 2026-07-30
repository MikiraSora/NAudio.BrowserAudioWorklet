using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAudio.Wave;
using NAudio.Wave.Browser;

namespace BrowserMusicPlayerDemo;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int SeekDebounceMilliseconds = 200;

    private static readonly string[] AudioExtensions = [".mp3", ".ogg", ".wav"];

    private readonly DispatcherTimer positionTimer;
    private readonly AsyncCommand addFilesCommand;
    private readonly AsyncCommand addFolderCommand;
    private readonly AsyncCommand playCommand;
    private readonly DelegateCommand pauseCommand;
    private readonly DelegateCommand stopCommand;

    private BrowserAudioWorkletPlayer? player;
    private PcmSampleProvider? provider;
    private TrackItem? currentTrack;
    private TrackItem? selectedTrack;
    private PlaybackState playbackState = PlaybackState.Stopped;
    private string status = "Pick audio files or a folder to build a playlist.";
    private string positionText = "0:00 / 0:00";
    private double volume = 0.8;
    private double seekSeconds;
    private double durationSeconds;
    private bool busy;
    private bool hasTracks;
    private bool seekPending;
    private bool seekUpdateFromTimer;
    private CancellationTokenSource? seekDebounce;
    private bool disposed;

    public MainViewModel()
    {
        addFilesCommand = new AsyncCommand(AddFilesAsync, () => !busy);
        addFolderCommand = new AsyncCommand(AddFolderAsync, () => !busy);
        playCommand = new AsyncCommand(PlayAsync, () =>
            !busy && (playbackState != PlaybackState.Playing ||
                      (selectedTrack != null && selectedTrack != currentTrack)));
        pauseCommand = new DelegateCommand(Pause, () => !busy && playbackState == PlaybackState.Playing);
        stopCommand = new DelegateCommand(Stop, () => !busy && playbackState != PlaybackState.Stopped);

        positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        positionTimer.Tick += OnPositionTimerTick;
        positionTimer.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Supplied by the view so the storage provider can pick files and folders.</summary>
    public TopLevel? TopLevel { get; set; }

    public ObservableCollection<TrackItem> Playlist { get; } = new();

    public bool HasTracks
    {
        get => hasTracks;
        private set => SetField(ref hasTracks, value);
    }

    public ICommand AddFilesCommand => addFilesCommand;

    public ICommand AddFolderCommand => addFolderCommand;

    public ICommand PlayCommand => playCommand;

    public ICommand PauseCommand => pauseCommand;

    public ICommand StopCommand => stopCommand;

    public TrackItem? SelectedTrack
    {
        get => selectedTrack;
        set
        {
            if (SetField(ref selectedTrack, value))
            {
                RefreshCommands();
            }
        }
    }

    public string Status
    {
        get => status;
        private set => SetField(ref status, value);
    }

    public string PositionText
    {
        get => positionText;
        private set => SetField(ref positionText, value);
    }

    public double Volume
    {
        get => volume;
        set
        {
            if (SetField(ref volume, value) && player != null)
            {
                player.Volume = (float)value;
            }
        }
    }

    /// <summary>
    /// Two-way seek slider value. Changes coming from the user are debounced into an
    /// actual seek; changes pushed by the position timer are ignored here.
    /// </summary>
    public double SeekSeconds
    {
        get => seekSeconds;
        set
        {
            if (!SetField(ref seekSeconds, value) || seekUpdateFromTimer)
            {
                return;
            }

            ScheduleSeek(value);
        }
    }

    public double DurationSeconds
    {
        get => durationSeconds;
        private set => SetField(ref durationSeconds, value);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        positionTimer.Stop();
        positionTimer.Tick -= OnPositionTimerTick;
        seekDebounce?.Cancel();
        DisposePlayer();
    }

    private async Task AddFilesAsync()
    {
        if (TopLevel == null)
        {
            return;
        }

        var files = await TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose audio files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio files")
                {
                    Patterns = new[] { "*.mp3", "*.ogg", "*.wav" }
                }
            }
        });

        int added = 0;
        foreach (var file in files)
        {
            if (IsAudioFile(file.Name))
            {
                Playlist.Add(new TrackItem(file));
                added++;
            }
        }

        if (added > 0)
        {
            HasTracks = true;
            SelectedTrack ??= Playlist.FirstOrDefault();
            Status = $"Added {added} track(s).";
        }
    }

    private async Task AddFolderAsync()
    {
        if (TopLevel == null)
        {
            return;
        }

        var folders = await TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a music folder",
            AllowMultiple = false
        });
        if (folders.Count == 0)
        {
            return;
        }

        SetBusy(true, "Scanning folder...");
        try
        {
            int added = await AddFolderRecursiveAsync(folders[0]);
            Status = added > 0
                ? $"Added {added} track(s)."
                : "No mp3/ogg/wav files found in that folder.";
            if (added > 0)
            {
                HasTracks = true;
                SelectedTrack ??= Playlist.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Status = $"Unable to read folder: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<int> AddFolderRecursiveAsync(IStorageFolder folder)
    {
        int added = 0;
        await foreach (var item in folder.GetItemsAsync())
        {
            if (item is IStorageFile file)
            {
                if (IsAudioFile(file.Name))
                {
                    Playlist.Add(new TrackItem(file));
                    added++;
                }
            }
            else if (item is IStorageFolder subFolder)
            {
                added += await AddFolderRecursiveAsync(subFolder);
            }
        }

        return added;
    }

    private async Task PlayAsync()
    {
        try
        {
            var track = selectedTrack ?? currentTrack ?? Playlist.FirstOrDefault();
            if (track == null)
            {
                Status = "Add audio files or a folder first.";
                return;
            }

            if (playbackState == PlaybackState.Paused && player != null && track == currentTrack)
            {
                await player.PlayAsync();
                SetPlaybackState(PlaybackState.Playing, PlayingMessage("Playing"));
                return;
            }

            if (track == currentTrack && provider != null)
            {
                // Same track already decoded: replay it from the start.
                SetBusy(true);
                try
                {
                    provider.PositionFrames = 0;
                    await StartNewRunAsync(false);
                    SetPlaybackState(PlaybackState.Playing, PlayingMessage("Playing"));
                }
                finally
                {
                    SetBusy(false);
                }

                return;
            }

            await LoadAndPlayAsync(track);
        }
        catch (Exception ex)
        {
            SetPlaybackState(PlaybackState.Stopped, $"Playback failed: {ex.Message}");
        }
    }

    private async Task LoadAndPlayAsync(TrackItem track)
    {
        SetBusy(true, $"Loading {track.Name}...");
        try
        {
            var decoded = await DecodeTrackAsync(track);
            seekDebounce?.Cancel();
            DisposePlayer();
            provider = new PcmSampleProvider(decoded);
            currentTrack = track;
            DurationSeconds = provider.LengthFrames / (double)provider.WaveFormat.SampleRate;
            await StartNewRunAsync(false);
            SetPlaybackState(PlaybackState.Playing, PlayingMessage("Playing"));
        }
        catch (Exception ex)
        {
            SetPlaybackState(PlaybackState.Stopped, $"Unable to play {track.Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task<DecodedAudio> DecodeTrackAsync(TrackItem track)
    {
        await using var stream = await track.File.OpenReadAsync();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return await AudioDecoder.DecodeAsync(memory.ToArray());
    }

    private void Pause()
    {
        seekDebounce?.Cancel();
        player?.Pause();
        SetPlaybackState(PlaybackState.Paused, PlayingMessage("Paused"));
    }

    private void Stop()
    {
        seekDebounce?.Cancel();
        DisposePlayer();
        if (provider != null)
        {
            provider.PositionFrames = 0;
        }

        SetPlaybackState(PlaybackState.Stopped, "Stopped");
        UpdatePositionDisplay();
    }

    private void ScheduleSeek(double seconds)
    {
        if (provider == null)
        {
            return;
        }

        seekDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        seekDebounce = cts;
        seekPending = true;
        _ = ApplySeekAfterDelayAsync(seconds, cts);
    }

    private async Task ApplySeekAfterDelayAsync(double seconds, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SeekDebounceMilliseconds, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            await SeekToAsync(TimeSpan.FromSeconds(seconds));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = $"Seek failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(seekDebounce, cts))
            {
                seekPending = false;
            }
        }
    }

    private async Task SeekToAsync(TimeSpan position)
    {
        if (provider == null || currentTrack == null)
        {
            return;
        }

        provider.PositionFrames = (long)Math.Round(position.TotalSeconds * provider.WaveFormat.SampleRate);
        UpdatePositionDisplay();
        if (playbackState == PlaybackState.Stopped)
        {
            return;
        }

        // The AudioWorklet ring buffer is fed ahead of time and has no flush, so a seek
        // during playback restarts the graph from the new position instead.
        bool resume = playbackState == PlaybackState.Playing;
        try
        {
            await StartNewRunAsync(!resume);
            SetPlaybackState(
                resume ? PlaybackState.Playing : PlaybackState.Paused,
                PlayingMessage(resume ? "Playing" : "Paused"));
        }
        catch (Exception ex)
        {
            SetPlaybackState(PlaybackState.Stopped, $"Seek failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts a fresh playback run for the current provider position. The library only
    /// accepts one Init per player, so every run (track change or seek) gets a new
    /// player; the old graph is torn down first.
    /// </summary>
    private async Task StartNewRunAsync(bool startPaused)
    {
        DisposePlayer();

        var next = new BrowserAudioWorkletPlayer();
        next.PlaybackStopped += OnPlaybackStopped;
        next.Init(provider!);
        next.Volume = (float)volume;
        player = next;
        await next.PlayAsync();
        if (startPaused)
        {
            next.Pause();
        }
    }

    private void DisposePlayer()
    {
        if (player == null)
        {
            return;
        }

        // Detach first: the explicit stop below raises PlaybackStopped synchronously and
        // must not be mistaken for a natural end of stream.
        player.PlaybackStopped -= OnPlaybackStopped;
        player.Stop();
        player.Dispose();
        player = null;
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, player))
        {
            return;
        }

        if (e.Exception != null)
        {
            SetPlaybackState(PlaybackState.Stopped, $"Playback failed: {RootMessage(e.Exception)}");
            return;
        }

        // Natural end of stream: advance to the next playlist entry.
        int index = currentTrack == null ? -1 : Playlist.IndexOf(currentTrack);
        var next = index >= 0 && index + 1 < Playlist.Count ? Playlist[index + 1] : null;
        if (next != null)
        {
            SelectedTrack = next;
            _ = LoadAndPlayAsync(next);
        }
        else
        {
            if (provider != null)
            {
                provider.PositionFrames = 0;
            }

            SetPlaybackState(PlaybackState.Stopped, "Finished");
            UpdatePositionDisplay();
        }
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (provider == null)
        {
            return;
        }

        UpdatePositionDisplay();
        if (seekPending)
        {
            return;
        }

        seekUpdateFromTimer = true;
        try
        {
            SeekSeconds = provider.PositionFrames / (double)provider.WaveFormat.SampleRate;
        }
        finally
        {
            seekUpdateFromTimer = false;
        }
    }

    private void UpdatePositionDisplay()
    {
        if (provider == null)
        {
            PositionText = "0:00 / 0:00";
            return;
        }

        double position = provider.PositionFrames / (double)provider.WaveFormat.SampleRate;
        double duration = provider.LengthFrames / (double)provider.WaveFormat.SampleRate;
        PositionText = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private string PlayingMessage(string verb)
        => currentTrack == null ? verb : $"{verb} {currentTrack.Name}";

    private void SetBusy(bool value, string? statusMessage = null)
    {
        busy = value;
        if (statusMessage != null)
        {
            Status = statusMessage;
        }

        RefreshCommands();
    }

    private void SetPlaybackState(PlaybackState state, string message)
    {
        playbackState = state;
        Status = message;
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        addFilesCommand.RaiseCanExecuteChanged();
        addFolderCommand.RaiseCanExecuteChanged();
        playCommand.RaiseCanExecuteChanged();
        pauseCommand.RaiseCanExecuteChanged();
        stopCommand.RaiseCanExecuteChanged();
    }

    private static bool IsAudioFile(string name)
        => AudioExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    private static string RootMessage(Exception error)
    {
        while (error.InnerException != null)
        {
            error = error.InnerException;
        }

        return error.Message;
    }

    private static string FormatTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.Hours > 0
            ? $"{value.Hours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";
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
