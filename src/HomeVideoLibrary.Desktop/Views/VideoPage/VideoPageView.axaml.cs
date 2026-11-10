using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.UI.Audio;
using HomeVideoLibrary.UI.Controls;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

/// <summary>The video page. Code here is limited to keyboard shortcuts, focus, the inspector's drag handle, pointer
/// activity over the video, and applying fullscreen to the window.</summary>
public sealed partial class VideoPageView : UserControl
{
    private readonly ShortcutRouter _shortcuts;
    private Window? _window;
    private IDisposable? _windowState;
    private WindowState _restoreState = WindowState.Normal;

    public VideoPageView()
    {
        InitializeComponent();
        _shortcuts = MapShortcuts(new ShortcutRouter(
            Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control));
        _shortcuts.Attach(this);
        Overlay.AttachedToVisualTree += (_, _) => { if (TopLevel.GetTopLevel(Overlay) is Window overlayWindow) ConnectOverlay(overlayWindow); };
        Overlay.PointerMoved += (_, _) => ViewModel?.Fullscreen.Wake();
        Overlay.DoubleTapped += (_, _) => ViewModel?.Fullscreen.ToggleCommand.Execute(null);
        FullscreenControls.PointerEntered += (_, _) => ViewModel?.Fullscreen.Hold(true);
        FullscreenControls.PointerExited += (_, _) => ViewModel?.Fullscreen.Hold(false);
        InspectorSplitter.DragDelta += (_, args) => { if (ViewModel is { } page) page.InspectorWidth -= args.Vector.X; };
        AddHandler(GotFocusEvent, SelectAllOnTab, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, LeaveTextEntryOnEscape, RoutingStrategies.Bubble);
        Loaded += (_, _) => Focus();
    }

    private VideoPageViewModel? ViewModel => DataContext as VideoPageViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null || ViewModel is not { } page) return;
        _windowState = _window.GetObservable(Window.WindowStateProperty).Subscribe(new WindowStateObserver(this));
        page.Fullscreen.PropertyChanged += FullscreenChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _windowState?.Dispose();
        _windowState = null;
        if (ViewModel is { } page)
        {
            page.Fullscreen.PropertyChanged -= FullscreenChanged;
            // Leaving the page leaves fullscreen with it.
            if (page.Fullscreen.IsFullscreen && _window is { WindowState: WindowState.FullScreen }) _window.WindowState = _restoreState;
        }
        _window = null;
    }

    /// <summary>Applies the page's fullscreen request to the window.</summary>
    private void FullscreenChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(FullscreenViewModel.IsFullscreen) || _window is not { } window || ViewModel is not { } page) return;
        if (page.Fullscreen.IsFullscreen && window.WindowState != WindowState.FullScreen)
        {
            _restoreState = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
        else if (!page.Fullscreen.IsFullscreen && window.WindowState == WindowState.FullScreen) window.WindowState = _restoreState;
    }

    /// <summary>Wires the window LibVLCSharp shows the overlay in. Over the video, input goes to that window (it may become
    /// the key window), so its keys route through the page's shortcuts too; and each time it is shown it is tied to the
    /// page's window so it follows it into fullscreen.</summary>
    private void ConnectOverlay(Window overlayWindow)
    {
        _shortcuts.Attach(overlayWindow);
        overlayWindow.GetObservable(IsVisibleProperty).Subscribe(new OverlayShownObserver(this, overlayWindow));
    }

    private sealed class OverlayShownObserver(VideoPageView view, Window overlayWindow) : IObserver<bool>
    {
        public void OnNext(bool shown)
        {
            if (shown && TopLevel.GetTopLevel(view) is Window owner) MacChildWindow.Attach(owner, overlayWindow);
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    /// <summary>Reports fullscreen the window enters or leaves by itself (the system's own control).</summary>
    private sealed class WindowStateObserver(VideoPageView view) : IObserver<WindowState>
    {
        public void OnNext(WindowState state)
        {
            if (view.ViewModel is { } page) page.Fullscreen.IsFullscreen = state == WindowState.FullScreen;
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private ShortcutRouter MapShortcuts(ShortcutRouter router)
    {
        Func<ICommand?> Player(Func<PlayerViewModel, ICommand> command) => () => ViewModel is { } page ? command(page.Player) : null;
        Func<ICommand?> Page(Func<VideoPageViewModel, ICommand> command) => () => ViewModel is { } page ? command(page) : null;
        var skip = Player(player => player.SkipCommand);
        router
            .Map(Key.Space, KeyModifiers.None, Player(player => player.PlayPauseCommand), ClickSoundKind.Plastic)
            .Map(Key.K, KeyModifiers.None, Player(player => player.PlayPauseCommand), ClickSoundKind.Plastic)
            .Map(Key.Left, KeyModifiers.None, skip, parameter: -PlayerViewModel.ShortStep)
            .Map(Key.Right, KeyModifiers.None, skip, parameter: PlayerViewModel.ShortStep)
            .Map(Key.Left, KeyModifiers.Shift, skip, parameter: -PlayerViewModel.FineStep)
            .Map(Key.Right, KeyModifiers.Shift, skip, parameter: PlayerViewModel.FineStep)
            .Map(Key.J, KeyModifiers.None, skip, parameter: -PlayerViewModel.LongStep)
            .Map(Key.L, KeyModifiers.None, skip, parameter: PlayerViewModel.LongStep)
            .Map(Key.Home, KeyModifiers.None, Player(player => player.SeekCommand), parameter: TimeSpan.Zero)
            .Map(Key.End, KeyModifiers.None, Player(player => player.SeekCommand), parameter: TimeSpan.MaxValue)
            .Map(Key.OemOpenBrackets, KeyModifiers.None, Page(page => page.Details.Chapters.PreviousChapterCommand))
            .Map(Key.OemCloseBrackets, KeyModifiers.None, Page(page => page.Details.Chapters.NextChapterCommand))
            .Map(Key.PageUp, KeyModifiers.None, Page(page => page.Details.Chapters.PreviousChapterCommand))
            .Map(Key.PageDown, KeyModifiers.None, Page(page => page.Details.Chapters.NextChapterCommand))
            .Map(Key.Up, KeyModifiers.None, Player(player => player.ChangeVolumeCommand), parameter: PlayerViewModel.VolumeStep)
            .Map(Key.Down, KeyModifiers.None, Player(player => player.ChangeVolumeCommand), parameter: -PlayerViewModel.VolumeStep)
            .Map(Key.D0, KeyModifiers.None, Player(player => player.ToggleMuteCommand), ClickSoundKind.Plastic)
            .Map(Key.NumPad0, KeyModifiers.None, Player(player => player.ToggleMuteCommand), ClickSoundKind.Plastic)
            .Map(Key.M, KeyModifiers.None, Page(page => page.AddChapterCommand), ClickSoundKind.Rubber)
            .Map(Key.F2, KeyModifiers.None, Page(page => page.Details.Chapters.RenameCurrentCommand))
            .Map(Key.C, KeyModifiers.Shift, Page(page => page.Details.Frames.CaptureCommand), ClickSoundKind.Plastic)
            .Map(Key.F, KeyModifiers.None, Page(page => page.Fullscreen.ToggleCommand), ClickSoundKind.Plastic)
            .Map(Key.Escape, KeyModifiers.None, Page(page => page.Fullscreen.ExitCommand))
            .Map(Key.Escape, KeyModifiers.None, Page(page => page.BackCommand), ClickSoundKind.Plastic)
            .Map(Key.Delete, KeyModifiers.None, () => FocusedChapter()?.DeleteCommand)
            .Map(Key.Back, KeyModifiers.None, () => FocusedChapter()?.DeleteCommand)
            .MapCommand(Key.S, Page(page => page.Details.SaveCommand), ClickSoundKind.Plastic)
            .MapCommand(Key.OemPipe, Page(page => page.ToggleInspectorCommand), ClickSoundKind.Plastic)
            .MapCommand(Key.OemBackslash, Page(page => page.ToggleInspectorCommand), ClickSoundKind.Plastic);
        Key[] tabKeys = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5];
        for (var tab = 0; tab < tabKeys.Length; tab++)
            router.MapCommand(tabKeys[tab], Page(page => page.SelectTabCommand), ClickSoundKind.Plastic, parameter: tab);
        return router;
    }

    private ChapterRowViewModel? FocusedChapter() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is ChapterListItem { DataContext: ChapterRowViewModel row } ? row : null;

    // Tabbing into a single-line field selects its text, so typing replaces it; a click places the caret instead.
    private static void SelectAllOnTab(object? sender, GotFocusEventArgs args)
    {
        if (args.NavigationMethod == NavigationMethod.Tab && args.Source is TextBox { AcceptsReturn: false } field) field.SelectAll();
    }

    // Escape leaves a text field (keeping its text) so the page's shortcuts work again.
    private void LeaveTextEntryOnEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || args.Handled || !ShortcutRouter.IsTextEntry(args.Source)) return;
        args.Handled = true;
        Focus();
    }
}
