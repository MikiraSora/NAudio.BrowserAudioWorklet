using Avalonia.Platform.Storage;

namespace BrowserMusicPlayerDemo;

/// <summary>
/// One playlist entry. The storage file stays alive so the track can be decoded
/// whenever the user starts playing it.
/// </summary>
public sealed class TrackItem(IStorageFile file)
{
    public IStorageFile File { get; } = file;

    public string Name => File.Name;

    public override string ToString() => Name;
}
