using LibVLCSharp.Shared;
using System.Runtime.InteropServices;

namespace HomeVideoLibrary.Desktop.Playback;

public sealed class LibVlcPlayback : IVideoPlayback
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibVLC? _engine;
    private Media? _media;
    private long _version;
    public event EventHandler<PlaybackUpdate>? Updated;
    public long Version => Interlocked.Read(ref _version);
    public MediaPlayer? NativePlayer { get; private set; }

    public Task OpenAsync(string path, TimeSpan position, bool play, Func<Task> attachPlayer, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ClosePlayer();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_engine is null)
            {
                var directory = Environment.GetEnvironmentVariable("HOME_VIDEO_LIBRARY_LIBVLC");
                if (string.IsNullOrWhiteSpace(directory) && OperatingSystem.IsMacOS() && Directory.Exists("/Applications/VLC.app/Contents/MacOS/lib"))
                    directory = "/Applications/VLC.app/Contents/MacOS/lib";
                if (OperatingSystem.IsMacOS() && directory is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")))
                {
                    var plugins = new[] { Path.Combine(directory, "plugins"), Path.Combine(directory, "..", "plugins"), Path.Combine(directory, "vlc", "plugins") }
                        .Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
                    // Configure libc before LibVLC loads; managed environment changes alone do not
                    // reach native getenv on this host. Respect any explicit startup plugin path.
                    if (plugins is not null && SetNativeEnvironment("VLC_PLUGIN_PATH", plugins, 1) != 0)
                        throw new InvalidOperationException("Could not configure the LibVLC plugin directory.");
                }
                LibVLCSharp.Shared.Core.Initialize(directory);
                _engine = new LibVLC("--no-video-title-show", "--no-metadata-network-access");
            }
            var version = Version;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new MediaPlayer(_engine);
            player.EnableKeyInput = false;
            player.EnableMouseInput = false;
            NativePlayer = player;
            await attachPlayer(); // Bind the native surface before Play can create its own output window.
            player.TimeChanged += (_, args) => Updated?.Invoke(this, new PlaybackUpdate(version, Milliseconds: args.Time));
            player.Playing += (_, _) => { started.TrySetResult(); Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: true)); };
            player.Paused += (_, _) => Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false));
            player.EndReached += (_, _) => Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false));
            player.EncounteredError += (_, _) =>
            {
                started.TrySetException(new InvalidOperationException("The video could not be played. Check that the library file is readable."));
                Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false, Error: "Playback failed. Check the media file and native runtime."));
            };
            _media = new Media(_engine, new Uri(Path.GetFullPath(path)));
            if (!player.Play(_media)) throw new InvalidOperationException("The video could not be opened for playback.");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            player.Time = (long)position.TotalMilliseconds;
            if (!play) player.SetPause(true);
        }
        catch (OperationCanceledException) { await DetachAndCloseAsync(attachPlayer); throw; }
        catch (Exception exception)
        {
            await DetachAndCloseAsync(attachPlayer);
            throw new InvalidOperationException("Playback is unavailable. Install a matching LibVLC 3 runtime or set HOME_VIDEO_LIBRARY_LIBVLC to its native library folder (including plugins). The VLC desktop app is optional. " + exception.Message, exception);
        }
    });

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int SetNativeEnvironment(string name, string value, int overwrite);

    private async Task DetachAndCloseAsync(Func<Task> attachPlayer)
    {
        NativePlayer?.Stop();
        var player = NativePlayer;
        NativePlayer = null;
        await attachPlayer();
        NativePlayer = player;
        ClosePlayer();
    }

    public Task CloseAsync(Func<Task> detachPlayer) => RunAsync(async () =>
    {
        Interlocked.Increment(ref _version);
        NativePlayer?.Stop();
        await detachPlayer();
        ClosePlayer();
    });
    public Task SetPlayingAsync(bool play) => RunAsync(() =>
    {
        if (NativePlayer is { } player)
        {
            if (play) player.Play(); else player.SetPause(true);
        }
        return Task.CompletedTask;
    });
    public Task SeekAsync(TimeSpan position) => RunAsync(() =>
    {
        if (NativePlayer is { } player) player.Time = (long)position.TotalMilliseconds;
        return Task.CompletedTask;
    });
    public Task SetVolumeAsync(int volume) => RunAsync(() =>
    {
        if (NativePlayer is { } player) player.Volume = Math.Clamp(volume, 0, 100);
        return Task.CompletedTask;
    });

    private async Task RunAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try { await Task.Run(operation); }
        finally { _gate.Release(); }
    }

    private void ClosePlayer()
    {
        Interlocked.Increment(ref _version);
        var player = NativePlayer;
        NativePlayer = null;
        player?.Stop();
        player?.Dispose();
        _media?.Dispose();
        _media = null;
    }

    public async ValueTask DisposeAsync()
    {
        await RunAsync(() =>
        {
            ClosePlayer();
            _engine?.Dispose();
            _engine = null;
            return Task.CompletedTask;
        });
    }
}
