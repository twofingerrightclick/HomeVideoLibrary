namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A full-window page shown in place of the shelf. The shell creates one per visit (a video, a merge) and
/// disposes it on leaving, so per-visit state never outlives the visit. Pages are rendered through
/// <c>PageViews.Template</c>, which builds a new view for every page.</summary>
public abstract class PageViewModel : ViewModelBase, IAsyncDisposable
{
    private bool _isImmersive;

    /// <summary>Whether the page fills the window (for example fullscreen video); the shell hides its own chrome meanwhile.</summary>
    public bool IsImmersive { get => _isImmersive; protected set => SetProperty(ref _isImmersive, value); }

    /// <summary>Whether the page's own toolbar has a key back to the library. When it hasn't, the shell shows one, so
    /// no page is ever a dead end.</summary>
    public virtual bool HasBackKey => false;

    /// <summary>Whether the page shows or works on <paramref name="videoId"/>, so the video cannot leave the library
    /// while the page is open.</summary>
    public virtual bool Shows(Guid videoId) => false;

    /// <summary>Runs once after the page's view exists (so native surfaces can bind first).</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;

    /// <summary>Whether the user may leave now. A dirty page asks through <paramref name="interaction"/> and saves or
    /// discards; busy work keeps the page. Returns false to stay.</summary>
    public virtual Task<bool> CanLeaveAsync(IInteraction interaction) => Task.FromResult(true);

    /// <summary>Called before the window closes: cancels cancellable work and waits for work that must finish (a
    /// save), so <see cref="CanLeaveAsync"/> afterwards only has unsaved changes left to ask about.</summary>
    public virtual Task SettleForCloseAsync() => Task.CompletedTask;

    public abstract ValueTask DisposeAsync();
}
