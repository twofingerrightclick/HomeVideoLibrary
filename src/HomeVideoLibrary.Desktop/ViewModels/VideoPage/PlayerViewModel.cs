using HomeVideoLibrary.Desktop.Playback;
using LibVLCSharp.Shared;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>One page's playback. It owns the page's <see cref="IVideoPlayback"/> and is the only writer of
/// <see cref="Player"/>: set before playback starts, so LibVLC never opens its own window, and cleared before the player
/// closes. Transport never waits on metadata work: seeks, play and volume go straight to the player.</summary>
public sealed class PlayerViewModel : ViewModelBase, IAsyncDisposable
{
    public static readonly TimeSpan LongStep = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ShortStep = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan FineStep = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan RewindStep = -LongStep;
    public const double VolumeStep = 0.05;

    private readonly IVideoPlayback _playback;
    private readonly string _path;
    private readonly Action<Action> _post;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _lifetime = new();
    private MediaPlayer? _player;
    private TimeSpan _position;
    private bool _isPlaying, _isMuted;
    private double _volume;
    // The newest seek not yet sent; while one is in flight, later requests replace it so only the latest is sent next.
    private TimeSpan? _queuedSeek;
    private Task _seeking = Task.CompletedTask;

    /// <param name="post">Runs an action on the UI thread later; playback events arrive on LibVLC threads.</param>
    /// <param name="report">Shows a playback problem in the page status.</param>
    public PlayerViewModel(IVideoPlayback playback, string path, TimeSpan duration, Action<Action> post,
        Action<string> report, double volume = 0.8, bool isMuted = false)
    {
        _playback = playback; _path = path; Duration = duration; _post = post; _report = report;
        _volume = Math.Clamp(volume, 0, 1); _isMuted = isMuted;
        _playback.Updated += PlaybackUpdated;
        SeekCommand = new RelayCommand<TimeSpan>(Seek);
        SkipCommand = new RelayCommand<TimeSpan>(offset => Seek(Position + offset));
        PlayPauseCommand = new AsyncCommand(_ => TogglePlayingAsync(), onError: exception => _report(exception.Message));
        ChangeVolumeCommand = new RelayCommand<double>(delta => Volume += delta);
        ToggleMuteCommand = new RelayCommand(() => IsMuted = !IsMuted);
    }

    public MediaPlayer? Player { get => _player; private set => SetProperty(ref _player, value); }
    public TimeSpan Duration { get; }

    /// <summary>Where playback is, as last reported by the player, or the target of the latest seek until the player
    /// catches up.</summary>
    public TimeSpan Position { get => _position; private set => SetProperty(ref _position, value); }

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    /// <summary>0..1; applied to the player as it changes.</summary>
    public double Volume
    {
        get => _volume;
        set { if (SetProperty(ref _volume, Math.Clamp(value, 0, 1))) ApplyVolume(); }
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
    public AsyncCommand PlayPauseCommand { get; }
    /// <summary>Parameter: a signed change of <see cref="Volume"/>.</summary>
    public RelayCommand<double> ChangeVolumeCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }

    /// <summary>Creates the player, publishes it for the view to bind, then starts playback.</summary>
    public async Task OpenAsync(TimeSpan position, bool play, CancellationToken cancellationToken)
    {
        Player = await _playback.CreatePlayerAsync();
        try { await _playback.StartAsync(_path, position, play, cancellationToken); }
        catch
        {
            await CloseAsync();
            throw;
        }
        await _playback.SetVolumeAsync(EffectiveVolume);
        IsPlaying = play;
    }

    /// <summary>Runs <paramref name="work"/> with the player closed (so the file has no open reader), then reopens it at
    /// the latest position, playing if it was playing. Seeks made meanwhile move that position.</summary>
    public async Task WhileReleasedAsync(Func<Task> work)
    {
        var resume = IsPlaying;
        await CloseAsync();
        try { await work(); }
        finally
        {
            if (!_lifetime.IsCancellationRequested)
            {
                try { await OpenAsync(Position, resume, _lifetime.Token); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { _report(exception.Message); }
            }
        }
    }

    /// <summary>Moves playback to <paramref name="target"/> (clamped to the video). A seek is sent at once unless one is
    /// in flight; then only the newest target is sent after it, so a burst of seeks ends where the user stopped.</summary>
    public void Seek(TimeSpan target)
    {
        if (_lifetime.IsCancellationRequested) return;
        Position = Clamp(target);
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

    public TimeSpan Clamp(TimeSpan time) => Duration <= TimeSpan.Zero ? TimeSpan.Zero
        : time < TimeSpan.Zero ? TimeSpan.Zero
        : time >= Duration ? Duration - TimeSpan.FromMilliseconds(1)
        : time;

    private async Task TogglePlayingAsync()
    {
        if (Player is null)
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
        // A time from before a seek landed would pull the playhead back to where the user left.
        if (update.Milliseconds is { } milliseconds && _seeking.IsCompleted) Position = Clamp(TimeSpan.FromMilliseconds(milliseconds));
        if (update.IsPlaying is { } playing) IsPlaying = playing;
        if (update.Error is { } error) _report(error);
    });

    private async Task CloseAsync()
    {
        Player = null;
        IsPlaying = false;
        await _playback.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _playback.Updated -= PlaybackUpdated;
        await CloseAsync();
        await _playback.DisposeAsync();
    }
}
