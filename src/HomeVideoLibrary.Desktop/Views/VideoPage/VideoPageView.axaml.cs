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
    private const double SplitterKeyStep = 16;
    private readonly ShortcutRouter _shortcuts;
    private Window? _window;
    private VideoOverlayWindow? _overlay;
    private IDisposable? _windowShortcuts, _windowState;
    // The window state before this page made the window fullscreen; null when the page did not change it.
    private WindowState? _restoreState;

    public VideoPageView()
    {
        InitializeComponent();
        _shortcuts = MapShortcuts(new ShortcutRouter(
            Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control));
        InspectorSplitter.AddHandler(PointerMovedEvent, ResizeInspector, RoutingStrategies.Bubble, handledEventsToo: true);
        InspectorSplitter.KeyDown += ResizeInspectorWithKeys;
        AddHandler(GotFocusEvent, SelectAllOnTab, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, LeaveTextEntryOnEscape, RoutingStrategies.Bubble);
        Loaded += (_, _) => Focus();
    }

    private VideoPageViewModel? ViewModel => DataContext as VideoPageViewModel;

    /// <summary>Takes the page's shortcuts and fullscreen to the window while the page is shown. The shortcuts listen on
    /// the window, so they work whatever has focus, including nothing (after the focused field's tab closed).</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is null || ViewModel is not { } page) return;
        _windowShortcuts = _shortcuts.Attach(_window);
        _windowState = _window.GetObservable(Window.WindowStateProperty).Subscribe(new WindowStateObserver(this));
        page.Fullscreen.PropertyChanged += FullscreenChanged;
        _overlay = new VideoOverlayWindow { DataContext = page };
        // Every layout pass (resizes, fullscreen enter and exit, the inspector opening), every move of the window and
        // every change of screens can change where the video is on screen.
        _window.LayoutUpdated += PlaceOverlay;
        _window.PositionChanged += PlaceOverlay;
        _window.Screens.Changed += PlaceOverlay;
    }

    private void PlaceOverlay(object? sender, EventArgs args)
    {
        if (_window is { } window) _overlay?.Place(window, Video);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _windowShortcuts?.Dispose();
        _windowState?.Dispose();
        _windowShortcuts = _windowState = null;
        if (_window is { } window)
        {
            window.LayoutUpdated -= PlaceOverlay;
            window.PositionChanged -= PlaceOverlay;
            window.Screens.Changed -= PlaceOverlay;
        }
        _overlay?.Close();
        _overlay = null;
        if (ViewModel is { } page)
        {
            page.Fullscreen.PropertyChanged -= FullscreenChanged;
            // Leaving the page undoes the fullscreen it made, and only that.
            if (page.Fullscreen.IsFullscreen && _restoreState is { } restore && _window is { WindowState: WindowState.FullScreen })
                _window.WindowState = restore;
        }
        _restoreState = null;
        _window = null;
    }

    /// <summary>Applies the page's fullscreen request to the window, remembering what to restore. A window that was
    /// already fullscreen (the system's control) is left as it is.</summary>
    private void FullscreenChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(FullscreenViewModel.IsFullscreen) || _window is not { } window || ViewModel is not { } page) return;
        if (page.Fullscreen.IsFullscreen)
        {
            if (window.WindowState == WindowState.FullScreen) return;
            _restoreState = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
        else if (_restoreState is { } restore)
        {
            _restoreState = null;
            if (window.WindowState == WindowState.FullScreen) window.WindowState = restore;
        }
    }

    /// <summary>Ends the page's fullscreen when the window leaves fullscreen by itself (the system's control).</summary>
    private sealed class WindowStateObserver(VideoPageView view) : IObserver<WindowState>
    {
        public void OnNext(WindowState state)
        {
            if (state == WindowState.FullScreen || view.ViewModel is not { Fullscreen.IsFullscreen: true } page) return;
            view._restoreState = null;
            page.Fullscreen.IsFullscreen = false;
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    // The handle sits at the inspector's left edge, so the inspector's width is the body's right edge minus the pointer.
    private void ResizeInspector(object? sender, PointerEventArgs args)
    {
        if (ViewModel is not { } page || !args.GetCurrentPoint(InspectorSplitter).Properties.IsLeftButtonPressed) return;
        page.InspectorWidth = Body.Bounds.Width - args.GetPosition(Body).X - InspectorSplitter.Bounds.Width / 2;
    }

    private void ResizeInspectorWithKeys(object? sender, KeyEventArgs args)
    {
        if (ViewModel is not { } page || args.Key is not (Key.Left or Key.Right)) return;
        args.Handled = true;
        page.InspectorWidth += args.Key == Key.Left ? SplitterKeyStep : -SplitterKeyStep;
    }

    private ShortcutRouter MapShortcuts(ShortcutRouter router)
    {
        Func<ICommand?> Player(Func<PlayerViewModel, ICommand> command) => () => ViewModel is { } page ? command(page.Player) : null;
        Func<ICommand?> Page(Func<VideoPageViewModel, ICommand> command) => () => ViewModel is { } page ? command(page) : null;
        var skip = Player(player => player.SkipCommand);
        router
            .Map(Key.Space, KeyModifiers.None, Player(player => player.PlayPauseCommand), ClickSoundKind.Plastic)
            .Map(Key.K, KeyModifiers.None, Player(player => player.PlayPauseCommand), ClickSoundKind.Plastic)
            .Map(Key.Left, KeyModifiers.None, skip, parameter: -PlayerViewModel.ShortStep, repeats: true)
            .Map(Key.Right, KeyModifiers.None, skip, parameter: PlayerViewModel.ShortStep, repeats: true)
            .Map(Key.Left, KeyModifiers.Shift, skip, parameter: -PlayerViewModel.FineStep, repeats: true)
            .Map(Key.Right, KeyModifiers.Shift, skip, parameter: PlayerViewModel.FineStep, repeats: true)
            .Map(Key.J, KeyModifiers.None, skip, parameter: -PlayerViewModel.LongStep, repeats: true)
            .Map(Key.L, KeyModifiers.None, skip, parameter: PlayerViewModel.LongStep, repeats: true)
            .Map(Key.Home, KeyModifiers.None, Player(player => player.SeekCommand), parameter: TimeSpan.Zero)
            .Map(Key.End, KeyModifiers.None, Player(player => player.SeekCommand), parameter: TimeSpan.MaxValue)
            .Map(Key.OemOpenBrackets, KeyModifiers.None, Page(page => page.Details.Chapters.PreviousChapterCommand))
            .Map(Key.OemCloseBrackets, KeyModifiers.None, Page(page => page.Details.Chapters.NextChapterCommand))
            .Map(Key.PageUp, KeyModifiers.None, Page(page => page.Details.Chapters.PreviousChapterCommand))
            .Map(Key.PageDown, KeyModifiers.None, Page(page => page.Details.Chapters.NextChapterCommand))
            .Map(Key.Up, KeyModifiers.None, Player(player => player.ChangeVolumeCommand), parameter: PlayerViewModel.VolumeStep, repeats: true)
            .Map(Key.Down, KeyModifiers.None, Player(player => player.ChangeVolumeCommand), parameter: -PlayerViewModel.VolumeStep, repeats: true)
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
