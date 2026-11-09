using System.Globalization;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Desktop.Playback;

public sealed class LibVlcPlayback : IVideoPlayback
{
    private static readonly Lock EngineLock = new();
    // One engine for the app's lifetime: creating LibVLC scans every plugin, which made each editor open slow.
    private static LibVLC? _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Media? _media;
    private long _version;
    public event EventHandler<PlaybackUpdate>? Updated;
    public long Version => Interlocked.Read(ref _version);
    public MediaPlayer? NativePlayer { get; private set; }

    public Task OpenAsync(string path, TimeSpan position, bool play, Func<Task> attachPlayer, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        ClosePlayer();
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("The video file is missing from the library folder.", path);
        LibVLC engine;
        try { engine = GetEngine(); }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Playback is unavailable. Install a matching LibVLC 3 runtime or set HOME_VIDEO_LIBRARY_LIBVLC to its native library folder (including plugins). The VLC desktop app is optional. " + exception.Message, exception);
        }
        try
        {
            var version = Version;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var player = new MediaPlayer(engine);
            player.EnableKeyInput = false;
            player.EnableMouseInput = false;
            NativePlayer = player;
            await attachPlayer(); // Bind the native surface before Play can create its own output window.
            player.TimeChanged += (_, args) => Updated?.Invoke(this, new PlaybackUpdate(version, Milliseconds: args.Time));
            player.Playing += (_, _) => { started.TrySetResult(); Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: true)); };
            player.Paused += (_, _) => Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false));
            // Without these, a file that stops before playing left the open waiting for its 15 s timeout.
            player.Stopped += (_, _) => started.TrySetException(new InvalidOperationException("The video stopped before it started playing."));
            player.EndReached += (_, _) =>
            {
                started.TrySetException(new InvalidOperationException("The video ended before it started playing."));
                Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false));
            };
            player.EncounteredError += (_, _) =>
            {
                started.TrySetException(new InvalidOperationException("The video could not be played. Check that the library file is readable."));
                Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false, Error: "Playback failed. Check the media file and native runtime."));
            };
            _media = new Media(engine, new Uri(Path.GetFullPath(path)));
            // Starting at the position avoids decoding from zero and then jumping.
            _media.AddOption(string.Create(CultureInfo.InvariantCulture, $":start-time={position.TotalSeconds:0.###}"));
            if (!player.Play(_media)) throw new InvalidOperationException("The video could not be opened for playback.");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            // LibVLC must not be called from its own event callbacks, so a paused open pauses here, right after Playing.
            if (!play) player.SetPause(true);
            var target = (long)position.TotalMilliseconds;
            if (Math.Abs(player.Time - target) > 1000) player.Time = target;
        }
        catch (OperationCanceledException) { await DetachAndCloseAsync(attachPlayer); throw; }
        catch (Exception exception)
        {
            await DetachAndCloseAsync(attachPlayer);
            throw new InvalidOperationException(exception is TimeoutException
                ? "The video took too long to start playing." : "The video could not be played. " + exception.Message, exception);
        }
    });

    private static LibVLC GetEngine()
    {
        lock (EngineLock)
        {
            if (_engine is not null) return _engine;
            var directory = Environment.GetEnvironmentVariable("HOME_VIDEO_LIBRARY_LIBVLC");
            if (string.IsNullOrWhiteSpace(directory)) directory = FindInstalledVlc();
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
            return _engine = new LibVLC("--no-video-title-show", "--no-metadata-network-access");
        }
    }

    /// <summary>The library folder of a VLC desktop install, used when no explicit runtime path is configured.</summary>
    private static string? FindInstalledVlc()
    {
        if (OperatingSystem.IsMacOS())
            return Directory.Exists("/Applications/VLC.app/Contents/MacOS/lib") ? "/Applications/VLC.app/Contents/MacOS/lib" : null;
        if (OperatingSystem.IsWindows())
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "VideoLAN", "VLC"))
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "libvlc.dll")));
        return null;
    }

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
            return Task.CompletedTask;
        });
    }
}
