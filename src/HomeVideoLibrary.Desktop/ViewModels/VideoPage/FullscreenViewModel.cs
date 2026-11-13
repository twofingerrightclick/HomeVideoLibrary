namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>Fullscreen playback and the controls drawn over it. They show while the video is paused, while the pointer
/// rests on them, and until <see cref="IdleDelay"/> after the pointer last moved; otherwise they hide, with the pointer,
/// so only the video shows.</summary>
public sealed class FullscreenViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2.5);
    private readonly Func<bool> _isPlaying;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    // One deadline, pushed back by every pointer move; a single wait runs until it passes.
    private DateTimeOffset _hideAt;
    private Task _waiting = Task.CompletedTask;
    private bool _isFullscreen, _isHeld, _isMenuOpen;

    /// <param name="isPlaying">Whether the video plays; the owner calls <see cref="PlaybackChanged"/> when it changes.</param>
    public FullscreenViewModel(Func<bool> isPlaying, TimeProvider? time = null)
    {
        _isPlaying = isPlaying;
        _time = time ?? TimeProvider.System;
        ToggleCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        ExitCommand = new RelayCommand(() => IsFullscreen = false, () => IsFullscreen);
    }

    /// <summary>Whether the page fills the screen. The view applies it to the window.</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            if (!SetProperty(ref _isFullscreen, value)) return;
            ExitCommand.RaiseCanExecuteChanged();
            if (value) Wake();
            else ShownChanged();
        }
    }

    /// <summary>Whether the controls over the fullscreen video show.</summary>
    public bool AreControlsShown => IsFullscreen && (!_isPlaying() || _isHeld || _isMenuOpen || _time.GetUtcNow() < _hideAt);

    /// <summary>Fullscreen with the controls hidden: the pointer hides too.</summary>
    public bool IsIdle => IsFullscreen && !AreControlsShown;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand ExitCommand { get; }

    /// <summary>The pointer moved: show the controls until <see cref="IdleDelay"/> from now.</summary>
    public void Wake()
    {
        if (_lifetime.IsCancellationRequested || !IsFullscreen) return;
        var wasShown = AreControlsShown;
        _hideAt = _time.GetUtcNow() + IdleDelay;
        if (!wasShown) ShownChanged();
        if (_waiting.IsCompleted) _waiting = HideWhenIdleAsync();
    }

    /// <summary>Whether the pointer is over the controls, which keeps them shown.</summary>
    public void Hold(bool held)
    {
        _isHeld = held;
        ShownChanged();
    }

    /// <summary>An open transport menu keeps the deck visible even when the pointer enters its popup.</summary>
    public void HoldMenu(bool open)
    {
        _isMenuOpen = open;
        if (!open) Wake();
        ShownChanged();
    }

    public void PlaybackChanged() => ShownChanged();

    private async Task HideWhenIdleAsync()
    {
        try
        {
            for (var left = _hideAt - _time.GetUtcNow(); left > TimeSpan.Zero; left = _hideAt - _time.GetUtcNow())
                await Task.Delay(left, _time, _lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        ShownChanged();
    }

    private void ShownChanged()
    {
        OnPropertyChanged(nameof(AreControlsShown));
        OnPropertyChanged(nameof(IsIdle));
    }

    public void Dispose()
    {
        if (_lifetime.IsCancellationRequested) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
