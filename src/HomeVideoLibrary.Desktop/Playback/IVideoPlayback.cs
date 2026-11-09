using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Desktop.Playback;

public sealed record PlaybackUpdate(long Version, long? Milliseconds = null, bool? IsPlaying = null, string? Error = null);

/// <summary>One video's native player. Opening is two-phase so the caller can bind the returned player to its video
/// surface before playback starts, without callbacks from the playback thread.</summary>
public interface IVideoPlayback : IAsyncDisposable
{
    /// <summary>Raised on a LibVLC thread. Updates whose <see cref="PlaybackUpdate.Version"/> differs from <see cref="Version"/>
    /// come from a closed player and must be ignored.</summary>
    event EventHandler<PlaybackUpdate>? Updated;
    long Version { get; }

    /// <summary>Closes any previous player and creates a new, idle one.</summary>
    Task<MediaPlayer> CreatePlayerAsync();

    /// <summary>Plays <paramref name="path"/> on the created player from <paramref name="position"/>, paused unless
    /// <paramref name="play"/>. On failure the player stays created; the caller unbinds it and calls <see cref="CloseAsync"/>.</summary>
    Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken);

    /// <summary>Stops and disposes the player. Unbind it from the video surface first.</summary>
    Task CloseAsync();
    Task SetPlayingAsync(bool play);
    Task SeekAsync(TimeSpan position);
    Task SetVolumeAsync(int volume);
}
