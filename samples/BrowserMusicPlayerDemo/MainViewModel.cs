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
    private const int DecodedTrackCacheSize = 2;

    private static readonly string[] AudioExtensions = [".mp3", ".ogg", ".wav"];

    private readonly DispatcherTimer positionTimer;
    private readonly AsyncCommand addFilesCommand;
    private readonly AsyncCommand addFolderCommand;
    private readonly AsyncCommand playCommand;
    private readonly DelegateCommand pauseCommand;
    private readonly AsyncCommand stopCommand;
    private readonly AsyncCommand resetConsumedCommand;
    private readonly object decodeCacheSync = new();
    private readonly Dictionary<TrackItem, Task<DecodedAudio>> decodedTracks = new();
    private readonly LinkedList<TrackItem> decodedTrackLru = new();
    private readonly SemaphoreSlim trackChangeLock = new(1, 1);

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
    private TimeSpan playbackPositionBase;
    private long totalConsumedFrameCount;
    private long totalConsumedSampleCount;
    private TimeSpan totalConsumedTime;
    private bool busy;
    private bool hasTracks;
    private bool seekPending;
    private bool seekUpdateFromTimer;
    private bool suppressPlaybackStopped;
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
        stopCommand = new AsyncCommand(StopAsync, () => !busy && playbackState != PlaybackState.Stopped);
        resetConsumedCommand = new AsyncCommand(
            ResetConsumedAsync,
            () => !busy && player != null);

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

    public ICommand ResetConsumedCommand => resetConsumedCommand;

    public TrackItem? SelectedTrack
    {
        get => selectedTrack;
        set
        {
            if (SetField(ref selectedTrack, value))
            {
                PrefetchTrack(value);
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
        positionTimer.Stop();
        positionTimer.Tick -= OnPositionTimerTick;
        seekDebounce?.Cancel();
        DisposePlayer();
        lock (decodeCacheSync)
        {
            decodedTracks.Clear();
            decodedTrackLru.Clear();
        }
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
                // Same track already decoded: reuse the prepared graph and current position.
                SetBusy(true);
                try
                {
                    await StartNewRunAsync(false);
                    SetPlaybackState(PlaybackState.Playing, PlayingMessage("Playing"));
                    PrefetchNextTrack();
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
            seekDebounce?.Cancel();
            await EnsureTrackReadyAsync(track);
            await StartNewRunAsync(false);
            SetPlaybackState(PlaybackState.Playing, PlayingMessage("Playing"));
            PrefetchNextTrack();
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

    private Task<DecodedAudio> DecodeTrackAsync(TrackItem track)
    {
        Task<DecodedAudio> decodeTask;
        lock (decodeCacheSync)
        {
            if (!decodedTracks.TryGetValue(track, out decodeTask!))
            {
                decodeTask = DecodeTrackCoreAsync(track);
                decodedTracks.Add(track, decodeTask);
            }

            decodedTrackLru.Remove(track);
            decodedTrackLru.AddFirst(track);
            while (decodedTrackLru.Count > DecodedTrackCacheSize)
            {
                TrackItem expired = decodedTrackLru.Last!.Value;
                decodedTrackLru.RemoveLast();
                decodedTracks.Remove(expired);
            }
        }

        return ObserveDecodeAsync(track, decodeTask);
    }

    private async Task<DecodedAudio> ObserveDecodeAsync(
        TrackItem track,
        Task<DecodedAudio> decodeTask)
    {
        try
        {
            return await decodeTask;
        }
        catch
        {
            lock (decodeCacheSync)
            {
                if (decodedTracks.TryGetValue(track, out Task<DecodedAudio>? cached) &&
                    ReferenceEquals(cached, decodeTask))
                {
                    decodedTracks.Remove(track);
                    decodedTrackLru.Remove(track);
                }
            }

            throw;
        }
    }

    private static async Task<DecodedAudio> DecodeTrackCoreAsync(TrackItem track)
    {
        await using var stream = await track.File.OpenReadAsync();
        int capacity = stream.CanSeek && stream.Length <= int.MaxValue
            ? checked((int)stream.Length)
            : 0;
        using var memory = capacity > 0 ? new MemoryStream(capacity) : new MemoryStream();
        await stream.CopyToAsync(memory);
        if (!memory.TryGetBuffer(out ArraySegment<byte> fileBytes) || fileBytes.Array == null)
        {
            return await AudioDecoder.DecodeAsync(memory.ToArray(), 0, checked((int)memory.Length));
        }

        return await AudioDecoder.DecodeAsync(
            fileBytes.Array,
            fileBytes.Offset,
            checked((int)memory.Length));
    }

    private void PrefetchTrack(TrackItem? track)
    {
        if (track != null)
        {
            _ = PrefetchTrackAsync(track);
        }
    }

    private async Task PrefetchTrackAsync(TrackItem track)
    {
        try
        {
            DecodedAudio decoded = await DecodeTrackAsync(track);
            await EnsureTrackReadyAsync(track, decoded, preloadOnly: true);
        }
        catch
        {
            // Prefetch is opportunistic. A foreground play reports decode/preparation errors.
        }
    }

    private void PrefetchNextTrack()
    {
        int index = currentTrack == null ? -1 : Playlist.IndexOf(currentTrack);
        PrefetchTrack(index >= 0 && index + 1 < Playlist.Count ? Playlist[index + 1] : null);
    }

    private void Pause()
    {
        seekDebounce?.Cancel();
        player?.Pause();
        SetPlaybackState(PlaybackState.Paused, PlayingMessage("Paused"));
    }

    private async Task StopAsync()
    {
        seekDebounce?.Cancel();
        Exception? resetError = null;
        if (player != null)
        {
            suppressPlaybackStopped = true;
            try
            {
                player.Stop();
            }
            finally
            {
                suppressPlaybackStopped = false;
            }

            try
            {
                await player.ResetTotalConsumedAsync();
            }
            catch (Exception ex)
            {
                resetError = ex;
            }
        }

        if (provider != null)
        {
            provider.Position = TimeSpan.Zero;
        }

        playbackPositionBase = TimeSpan.Zero;

        SetPlaybackState(
            PlaybackState.Stopped,
            resetError == null
                ? "Stopped"
                : $"Stopped; unable to reset consumed counter: {RootMessage(resetError)}");
        RefreshConsumed();
        UpdatePositionDisplay();
    }

    private async Task ResetConsumedAsync()
    {
        if (player == null)
        {
            return;
        }

        try
        {
            TimeSpan position = CurrentPlaybackPosition();
            await player.ResetTotalConsumedAsync();
            playbackPositionBase = position;
            RefreshConsumed();
            Status = "Consumed counter reset";
        }
        catch (Exception ex)
        {
            Status = $"Unable to reset consumed counter: {RootMessage(ex)}";
        }
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

        try
        {
            TimeSpan target = position > provider.Duration ? provider.Duration : position;
            if (player != null)
            {
                await player.SeekAsync(target);
                await player.ResetTotalConsumedAsync();
                playbackPositionBase = target;
            }
            else
            {
                provider.Position = target;
                playbackPositionBase = target;
            }

            RefreshConsumed();
            UpdatePositionDisplay();
        }
        catch (Exception ex)
        {
            SetPlaybackState(PlaybackState.Stopped, $"Seek failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts or resumes playback for the current provider position. Stops and seeks retain
    /// the prepared AudioContext and AudioWorkletNode; only a track-format change replaces them.
    /// </summary>
    private async Task StartNewRunAsync(bool startPaused)
    {
        if (player == null)
        {
            await EnsureTrackReadyAsync(currentTrack!);
        }

        await player!.ResetTotalConsumedAsync();
        playbackPositionBase = provider?.Position ?? TimeSpan.Zero;
        await player.PlayAsync();
        if (startPaused)
        {
            player.Pause();
        }
    }

    private async Task EnsureTrackReadyAsync(
        TrackItem track,
        DecodedAudio? prefetched = null,
        bool preloadOnly = false)
    {
        await trackChangeLock.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            if (currentTrack == track && provider != null && player != null)
            {
                return;
            }

            if (preloadOnly &&
                (selectedTrack != track || playbackState != PlaybackState.Stopped))
            {
                return;
            }

            DecodedAudio decoded = prefetched ?? await DecodeTrackAsync(track);
            if (preloadOnly &&
                (selectedTrack != track || playbackState != PlaybackState.Stopped))
            {
                return;
            }

            DisposePlayer();
            provider = new PcmSampleProvider(decoded);
            playbackPositionBase = TimeSpan.Zero;
            currentTrack = track;
            DurationSeconds = provider.Duration.TotalSeconds;
            UpdatePositionDisplay();

            var next = new BrowserAudioWorkletPlayer(BrowserAudioLatencyProfile.Balanced);
            next.PlaybackStopped += OnPlaybackStopped;
            next.Init(provider);
            next.Volume = (float)volume;
            player = next;
            try
            {
                await next.PrepareAsync();
            }
            catch
            {
                next.PlaybackStopped -= OnPlaybackStopped;
                next.Dispose();
                if (ReferenceEquals(player, next))
                {
                    player = null;
                }

                throw;
            }
        }
        finally
        {
            trackChangeLock.Release();
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

        RefreshConsumed();

        if (suppressPlaybackStopped)
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

        RefreshConsumed();
        UpdatePositionDisplay();
        if (seekPending)
        {
            return;
        }

        seekUpdateFromTimer = true;
        try
        {
            SeekSeconds = CurrentPlaybackPosition().TotalSeconds;
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

        double position = CurrentPlaybackPosition().TotalSeconds;
        double duration = provider.LengthFrames / (double)provider.WaveFormat.SampleRate;
        PositionText = $"{FormatTime(position)} / {FormatTime(duration)}";
    }

    private TimeSpan CurrentPlaybackPosition()
    {
        if (provider == null)
        {
            return TimeSpan.Zero;
        }

        TimeSpan position = player == null
            ? provider.Position
            : playbackPositionBase + player.TotalConsumedTime;
        return position < TimeSpan.Zero
            ? TimeSpan.Zero
            : position > provider.Duration ? provider.Duration : position;
    }

    private void RefreshConsumed()
    {
        if (player == null || disposed)
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
        resetConsumedCommand.RaiseCanExecuteChanged();
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
