using HomeVideoLibrary.Desktop.Playback;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>Where the page's player is in its life.</summary>
public enum PlaybackState
{
    /// <summary>Not opened yet.</summary>
    Idle,
    Opening,
    Open,
    /// <summary>Closed for a save that needs the file without a reader; it reopens when the save is done.</summary>
    Released,
    /// <summary>Opening failed; play tries again.</summary>
    Failed,
    Disposed,
}

/// <summary>One page's playback. It owns the page's <see cref="IVideoPlayback"/> and is the only writer of
/// <see cref="Surface"/>: set before playback starts, so LibVLC never opens its own window, and cleared before the player
/// closes. Opening and closing run one at a time, in order. Transport never waits on metadata work: seeks, play and
/// volume go straight to the player.</summary>
public sealed class PlayerViewModel : ViewModelBase, IAsyncDisposable
{
    public static readonly TimeSpan LongStep = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ShortStep = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FineStep = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RewindStep = -LongStep;
    public const double VolumeStep = 0.05;
    // After a seek, reported times this far from its target are from before it landed, unless the player keeps
    // reporting them for longer than SeekSettle (the seek did not take).
    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SeekSettle = TimeSpan.FromSeconds(2);

    private readonly IVideoPlayback _playback;
    private readonly string _path;
    private readonly Action<Action> _post;
    private readonly Action<string> _report;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _lifecycle = Task.CompletedTask;
    private VideoSurface? _surface;
    private PlaybackState _state;
    private TimeSpan _position;
    private bool _isPlaying, _isMuted;
    private double _volume;
    private double _speed = 1;
    // The newest seek not yet sent; while one is in flight, later requests replace it so only the latest is sent next.
    private TimeSpan? _queuedSeek;
    private Task _seeking = Task.CompletedTask;
    private (TimeSpan Target, DateTimeOffset At)? _lastSeek;

    /// <param name="post">Runs an action on the UI thread later; playback events arrive on LibVLC threads.</param>
    /// <param name="report">Shows a playback problem in the page status.</param>
    public PlayerViewModel(IVideoPlayback playback, string path, TimeSpan duration, Action<Action> post, Action<string> report,
        double volume = 0.8, bool isMuted = false, TimeProvider? time = null)
    {
        _playback = playback; _path = path; Duration = duration; _post = post; _report = report;
        _time = time ?? TimeProvider.System;
        _volume = Math.Clamp(volume, 0, 1); _isMuted = isMuted;
        _playback.Updated += PlaybackUpdated;
        SeekCommand = new RelayCommand<TimeSpan>(Seek);
        SkipCommand = new RelayCommand<TimeSpan>(offset => Seek(Position + offset));
        PlayPauseCommand = new AsyncCommand(_ => TogglePlayingAsync(), () => State is PlaybackState.Open or PlaybackState.Failed,
            onError: exception => _report(exception.Message));
        ChangeVolumeCommand = new RelayCommand<double>(delta => Volume += delta);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
    }

    /// <summary>The player the video view shows; null while there is none.</summary>
    public VideoSurface? Surface { get => _surface; private set => SetProperty(ref _surface, value); }

    public PlaybackState State
    {
        get => _state;
        private set { if (SetProperty(ref _state, value)) PlayPauseCommand.RaiseCanExecuteChanged(); }
    }

    public TimeSpan Duration { get; }

    /// <summary>Where playback is, as last reported by the player, or the target of the latest seek until the player
    /// reports from there.</summary>
    public TimeSpan Position { get => _position; private set => SetProperty(ref _position, value); }

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    /// <summary>0..1; applied to the player as it changes.</summary>
    public double Volume
    {
        get => _volume;
        set { if (SetProperty(ref _volume, Math.Clamp(value, 0, 1))) ApplyVolume(); }
    }

    public static IReadOnlyList<double> SpeedPresets { get; } = Array.AsReadOnly<double>([0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2]);

    /// <summary>Playback multiplier; retained when a metadata save releases and reopens the player.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            if (!double.IsFinite(value)) return;
            var previous = _speed;
            if (!SetProperty(ref _speed, Math.Clamp(value, 0.25, 2))) return;
            if (State == PlaybackState.Open) _ = ApplySpeedAsync(_speed, previous);
        }
    }

    private async Task ApplySpeedAsync(double speed, double previous)
    {
        try { await _playback.SetSpeedAsync(speed); }
        catch (Exception exception)
        {
            if (_speed == speed) SetProperty(ref _speed, previous, nameof(Speed));
            _report(exception.Message);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set { if (SetProperty(ref _isMuted, value)) ApplyVolume(); }
    }

    /// <summary>Parameter: the target <see cref="TimeSpan"/>. Never blocked; see <see cref="Seek"/>.</summary>
    public RelayCommand<TimeSpan> SeekCommand { get; }
    /// <summary>Parameter: a signed offset from the current position.</summary>
    public RelayCommand<TimeSpan> SkipCommand { get; }
    /// <summary>Plays or pauses an open player; after a failed open it tries again.</summary>
    public AsyncCommand PlayPauseCommand { get; }
    /// <summary>Parameter: a signed change of <see cref="Volume"/>.</summary>
    public RelayCommand<double> ChangeVolumeCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }

    /// <summary>Creates the player, publishes it for the view to bind, then starts playback at <paramref name="position"/>.
    /// Throws when it cannot start; the player is then closed again.</summary>
    public Task OpenAsync(TimeSpan position, bool play, CancellationToken cancellationToken)
    {
        Position = Clamp(position);
        return Enqueue(() => OpenCoreAsync(play, cancellationToken));
    }

    /// <summary>Closes the player, runs <paramref name="work"/> (which needs the file without a reader), and then starts
    /// reopening at the latest position, playing if it was playing. Completes when <paramref name="work"/> does; seeks
    /// made meanwhile move where playback resumes.</summary>
    public async Task WhileReleasedAsync(Func<Task> work)
    {
        var resume = IsPlaying;
        await Enqueue(() => CloseCoreAsync(PlaybackState.Released));
        try { await work(); }
        finally { _ = ReopenAsync(resume); }
    }

    /// <summary>Moves playback to <paramref name="target"/> (clamped to the video). A seek is sent at once unless one is
    /// in flight; then only the newest target is sent after it, so a burst of seeks ends where the user stopped.</summary>
    public void Seek(TimeSpan target)
    {
        if (_lifetime.IsCancellationRequested) return;
        Position = Clamp(target);
        _lastSeek = (Position, _time.GetUtcNow());
        _queuedSeek = Position;
        if (_seeking.IsCompleted) _seeking = SendSeeksAsync();
    }

    private async Task SendSeeksAsync()
    {
        while (_queuedSeek is { } target && !_lifetime.IsCancellationRequested)
        {
            _queuedSeek = null;
            try { await _playback.SeekAsync(target); }
            catch (Exception exception) { _report(exception.Message); }
        }
    }

    private TimeSpan Clamp(TimeSpan time) => Duration <= TimeSpan.Zero ? TimeSpan.Zero
        : time < TimeSpan.Zero ? TimeSpan.Zero
        : time >= Duration ? Duration - TimeSpan.FromMilliseconds(1)
        : time;

    /// <summary>Runs <paramref name="step"/> after every earlier open or close has finished, whatever its outcome.</summary>
    private Task Enqueue(Func<Task> step)
    {
        var previous = _lifecycle;
        var next = RunAfterAsync(previous, step);
        _lifecycle = next;
        return next;

        static async Task RunAfterAsync(Task previous, Func<Task> step)
        {
            try { await previous; }
            catch (Exception) { }
            await step();
        }
    }

    private async Task OpenCoreAsync(bool play, CancellationToken cancellationToken)
    {
        if (_lifetime.IsCancellationRequested) return;
        State = PlaybackState.Opening;
        var surface = await _playback.CreatePlayerAsync();
        // Disposal cleared the surface while this was creating it; the close queued after this releases the player.
        if (_lifetime.IsCancellationRequested) return;
        Surface = surface;
        try { await _playback.StartAsync(_path, Position, play, cancellationToken); }
        catch
        {
            await CloseCoreAsync(PlaybackState.Failed);
            throw;
        }
        await _playback.SetVolumeAsync(EffectiveVolume);
        await ApplySpeedAsync(Speed, 1);
        IsPlaying = play;
        State = PlaybackState.Open;
    }

    private async Task CloseCoreAsync(PlaybackState closed)
    {
        Surface = null;
        IsPlaying = false;
        if (!_lifetime.IsCancellationRequested) State = closed;
        await _playback.CloseAsync();
    }

    private async Task ReopenAsync(bool play)
    {
        if (_lifetime.IsCancellationRequested) return;
        var lifetime = _lifetime.Token;
        try { await Enqueue(() => OpenCoreAsync(play, lifetime)); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _report(exception.Message); }
    }

    private async Task TogglePlayingAsync()
    {
        if (State == PlaybackState.Failed)
        {
            await OpenAsync(Position, true, _lifetime.Token);
            return;
        }
        var play = !IsPlaying;
        await _playback.SetPlayingAsync(play);
        IsPlaying = play;
    }

    private int EffectiveVolume => IsMuted ? 0 : (int)Math.Round(Volume * 100);

    private void ApplyVolume() => _ = ApplyVolumeAsync(EffectiveVolume);

    private async Task ApplyVolumeAsync(int volume)
    {
        try { await _playback.SetVolumeAsync(volume); }
        catch (Exception exception) { _report(exception.Message); }
    }

    private void PlaybackUpdated(object? sender, PlaybackUpdate update) => _post(() =>
    {
        if (_lifetime.IsCancellationRequested || update.Version != _playback.Version) return;
        if (update.Milliseconds is { } milliseconds && AcceptedTime(TimeSpan.FromMilliseconds(milliseconds)) is { } time) Position = time;
        if (update.IsPlaying is { } playing) IsPlaying = playing;
        if (update.Error is { } error) _report(error);
    });

    /// <summary>The reported <paramref name="time"/>, clamped, unless it was reported from before the latest seek landed.</summary>
    private TimeSpan? AcceptedTime(TimeSpan time)
    {
        if (_lastSeek is { } seek)
        {
            if ((time - seek.Target).Duration() > SeekTolerance && _time.GetUtcNow() - seek.At < SeekSettle) return null;
            _lastSeek = null;
        }
        return Clamp(time);
    }

    /// <summary>Clears the surface before anything else, then closes and releases the player once any open in progress
    /// has finished.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _playback.Updated -= PlaybackUpdated;
        Surface = null;
        State = PlaybackState.Disposed;
        try { await Enqueue(() => CloseCoreAsync(PlaybackState.Disposed)); }
        finally
        {
            try { await _playback.DisposeAsync(); }
            finally { _lifetime.Dispose(); }
        }
    }
}
