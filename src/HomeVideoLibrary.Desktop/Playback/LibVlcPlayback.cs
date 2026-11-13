using System.Globalization;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Desktop.Playback;

public sealed class LibVlcPlayback : IVideoPlayback
{
    private static readonly Lock EngineLock = new();
    // One engine for the app's lifetime: creating LibVLC scans every plugin, which made each editor open slow.
    private static LibVLC? _engine;
    private static bool _nativeResolverInstalled;
    private static string? _nativeLibraryPath;
    // Like the shared engine, the resolved library stays loaded for the application's lifetime.
    private static IntPtr _nativeLibraryHandle;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaPlayer? _player;
    private Media? _media;
    private TaskCompletionSource? _started;
    private long _version;
    public event EventHandler<PlaybackUpdate>? Updated;
    public long Version => Interlocked.Read(ref _version);

    public Task<VideoSurface> CreatePlayerAsync() => RunAsync(() =>
    {
        ClosePlayer();
        LibVLC engine;
        try { engine = GetEngine(); }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Playback is unavailable. Install a matching LibVLC 3 runtime or set HOME_VIDEO_LIBRARY_LIBVLC to its native library folder (including plugins). The VLC desktop app is optional. " + exception.Message, exception);
        }
        var version = Version;
        var player = new MediaPlayer(engine) { EnableKeyInput = false, EnableMouseInput = false };
        player.TimeChanged += (_, args) => Updated?.Invoke(this, new PlaybackUpdate(version, Milliseconds: args.Time));
        player.Playing += (_, _) => { Volatile.Read(ref _started)?.TrySetResult(); Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: true)); };
        player.Paused += (_, _) => Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false));
        // Without these, a file that stops before playing left the start waiting for its 15 s timeout.
        player.Stopped += (_, _) => Volatile.Read(ref _started)?.TrySetException(new InvalidOperationException("The video stopped before it started playing."));
        player.EndReached += (_, _) =>
        {
            Volatile.Read(ref _started)?.TrySetException(new InvalidOperationException("The video ended before it started playing."));
            Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false));
        };
        player.EncounteredError += (_, _) =>
        {
            Volatile.Read(ref _started)?.TrySetException(new InvalidOperationException("Check that the library file is readable."));
            Updated?.Invoke(this, new PlaybackUpdate(version, IsPlaying: false, Error: "Playback failed. Check the media file and native runtime."));
        };
        _player = player;
        return Task.FromResult(new VideoSurface(player));
    });

    public Task StartAsync(string path, TimeSpan position, bool play, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var player = _player ?? throw new InvalidOperationException("Create the player before starting playback.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path)) throw new FileNotFoundException("The video file is missing from the library folder.", path);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _started, started);
        _media?.Dispose();
        _media = new Media(GetEngine(), new Uri(Path.GetFullPath(path)));
        // Starting at the position avoids decoding from zero and then jumping.
        _media.AddOption(string.Create(CultureInfo.InvariantCulture, $":start-time={position.TotalSeconds:0.###}"));
        try
        {
            if (!player.Play(_media)) throw new InvalidOperationException("The video could not be opened for playback.");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (TimeoutException exception) { throw new InvalidOperationException("The video took too long to start playing.", exception); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("The video could not be played. " + exception.Message, exception);
        }
        // LibVLC must not be called from its own event callbacks, so a paused start pauses here, right after Playing.
        if (!play) player.SetPause(true);
        var target = (long)position.TotalMilliseconds;
        if (Math.Abs(player.Time - target) > 1000) player.Time = target;
    });

    private static LibVLC GetEngine()
    {
        lock (EngineLock)
        {
            if (_engine is not null) return _engine;
            var directory = FindInstalledVlc(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("HOME_VIDEO_LIBRARY_LIBVLC"));
            if (OperatingSystem.IsMacOS() && directory is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH")))
            {
                var plugins = new[] { Path.Combine(directory, "plugins"), Path.Combine(directory, "..", "plugins"), Path.Combine(directory, "vlc", "plugins") }
                    .Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
                // Configure libc before LibVLC loads; managed environment changes alone do not
                // reach native getenv on this host. Respect any explicit startup plugin path.
                if (plugins is not null && SetNativeEnvironment("VLC_PLUGIN_PATH", plugins, 1) != 0)
                    throw new InvalidOperationException("Could not configure the LibVLC plugin directory.");
            }
            if (OperatingSystem.IsMacOS() && directory is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VLC_DATA_PATH")))
            {
                var share = Path.GetFullPath(Path.Combine(directory, "..", "share"));
                if (Directory.Exists(share) && SetNativeEnvironment("VLC_DATA_PATH", share, 1) != 0)
                    throw new InvalidOperationException("Could not configure the LibVLC data directory.");
            }
            if (OperatingSystem.IsMacOS() && directory is not null)
            {
                _nativeLibraryPath = Path.Combine(directory, "libvlc.dylib");
                if (!_nativeResolverInstalled)
                {
                    // Core.Initialize preloads dylibs, but .NET's short-name P/Invokes do not always
                    // match a relocated dylib's install name. Bind them to the selected runtime explicitly.
                    NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, _, _) =>
                    {
                        if (name != "libvlc") return IntPtr.Zero;
                        if (_nativeLibraryHandle == IntPtr.Zero) _nativeLibraryHandle = NativeLibrary.Load(_nativeLibraryPath!);
                        return _nativeLibraryHandle;
                    });
                    _nativeResolverInstalled = true;
                }
            }
            LibVLCSharp.Shared.Core.Initialize(directory);
            return _engine = new LibVLC("--no-video-title-show", "--no-metadata-network-access");
        }
    }

    /// <summary>An explicit runtime overrides the bundled engine; source builds can use an installed VLC.</summary>
    internal static string? FindInstalledVlc(string applicationDirectory, string? explicitDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory)) return explicitDirectory;
        if (OperatingSystem.IsMacOS())
        {
            var bundled = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "Resources", "Native", "vlc", "lib"));
            if (File.Exists(Path.Combine(bundled, "libvlc.dylib"))) return bundled;
            return Directory.Exists("/Applications/VLC.app/Contents/MacOS/lib") ? "/Applications/VLC.app/Contents/MacOS/lib" : null;
        }
        if (OperatingSystem.IsWindows())
            return new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "VideoLAN", "VLC"))
                .FirstOrDefault(candidate => File.Exists(Path.Combine(candidate, "libvlc.dll")));
        return null;
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int SetNativeEnvironment(string name, string value, int overwrite);

    public Task CloseAsync() => RunAsync(() =>
    {
        ClosePlayer();
        return Task.CompletedTask;
    });
    public Task SetPlayingAsync(bool play) => RunAsync(() =>
    {
        if (_player is { } player)
        {
            if (play) player.Play(); else player.SetPause(true);
        }
        return Task.CompletedTask;
    });
    public Task SeekAsync(TimeSpan position) => RunAsync(() =>
    {
        if (_player is { } player) player.Time = (long)position.TotalMilliseconds;
        return Task.CompletedTask;
    });
    public Task SetVolumeAsync(int volume) => RunAsync(() =>
    {
        if (_player is { } player) player.Volume = Math.Clamp(volume, 0, 100);
        return Task.CompletedTask;
    });

    public Task SetSpeedAsync(double speed) => RunAsync(() =>
    {
        if (_player is { } player && player.SetRate((float)Math.Clamp(speed, 0.25, 2)) == -1)
            throw new InvalidOperationException("The video could not be played at the selected speed.");
        return Task.CompletedTask;
    });

    private async Task RunAsync(Func<Task> operation) => await RunAsync(async () => { await operation(); return true; });

    private async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        await _gate.WaitAsync();
        try { return await Task.Run(operation); }
        finally { _gate.Release(); }
    }

    private void ClosePlayer()
    {
        Interlocked.Increment(ref _version);
        Volatile.Write(ref _started, null);
        var player = _player;
        _player = null;
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
