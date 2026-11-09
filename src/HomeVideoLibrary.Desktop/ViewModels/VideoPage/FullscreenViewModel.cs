namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>Fullscreen playback and the controls drawn over it. They show while the video is paused, while the pointer
/// rests on them, and for <see cref="IdleDelay"/> after the pointer last moved; otherwise they hide, with the pointer,
/// so only the video shows.</summary>
public sealed class FullscreenViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2.5);
    private readonly Func<bool> _isPlaying;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _idle;
    private bool _isFullscreen, _isAwake, _isHeld;

    /// <param name="isPlaying">Whether the video plays; the owner calls <see cref="PlaybackChanged"/> when it changes.</param>
    /// <param name="delay">Waits out the idle time; tests replace it.</param>
    public FullscreenViewModel(Func<bool> isPlaying, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _isPlaying = isPlaying;
        _delay = delay ?? Task.Delay;
        ToggleCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        ExitCommand = new RelayCommand(() => IsFullscreen = false, () => IsFullscreen);
    }

    /// <summary>Whether the page fills the screen. The view applies it to the window and reports changes the window
    /// makes itself (the system's own fullscreen control).</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (!SetProperty(ref _isFullscreen, value)) return;
            ExitCommand.RaiseCanExecuteChanged();
            Wake();
        }
    }

    /// <summary>Whether the controls over the fullscreen video show.</summary>
    public bool AreControlsShown => IsFullscreen && (!_isPlaying() || _isAwake || _isHeld);

    /// <summary>Fullscreen with the controls hidden: the pointer hides too.</summary>
    public bool IsIdle => IsFullscreen && !AreControlsShown;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand ExitCommand { get; }

    /// <summary>The pointer moved: show the controls and restart the idle countdown.</summary>
    public void Wake()
    {
        _idle?.Cancel();
        _idle?.Dispose();
        _idle = new CancellationTokenSource();
        _isAwake = true;
        ShownChanged();
        _ = SleepAsync(_idle.Token);
    }

    /// <summary>Whether the pointer is over the controls, which keeps them shown.</summary>
    public void Hold(bool held)
    {
        _isHeld = held;
        ShownChanged();
    }

    public void PlaybackChanged() => ShownChanged();

    private async Task SleepAsync(CancellationToken cancellationToken)
    {
        try { await _delay(IdleDelay, cancellationToken); }
        catch (OperationCanceledException) { return; }
        _isAwake = false;
        ShownChanged();
    }

    private void ShownChanged()
    {
        OnPropertyChanged(nameof(AreControlsShown));
        OnPropertyChanged(nameof(IsIdle));
    }

    public void Dispose()
    {
        _idle?.Cancel();
        _idle?.Dispose();
        _idle = null;
    }
}
