using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Desktop.Playback;

public sealed record PlaybackUpdate(long Version, long? Milliseconds = null, bool? IsPlaying = null, string? Error = null);

public interface IVideoPlayback : IAsyncDisposable
{
    event EventHandler<PlaybackUpdate>? Updated;
    long Version { get; }
    MediaPlayer? NativePlayer { get; }
    Task OpenAsync(string path, TimeSpan position, bool play, Func<Task> attachPlayer, CancellationToken cancellationToken);
    Task CloseAsync(Func<Task> detachPlayer);
    Task SetPlayingAsync(bool play);
    Task SeekAsync(TimeSpan position);
    Task SetVolumeAsync(int volume);
}
