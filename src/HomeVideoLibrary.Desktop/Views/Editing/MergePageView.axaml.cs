using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.Desktop.Views.Editing;

/// <summary>The merge page. Code here is limited to keyboard shortcuts, focus, and passing list clicks to the view
/// model as the selection.</summary>
public sealed partial class MergePageView : UserControl
{
    private readonly ShortcutRouter _shortcuts;
    private IDisposable? _windowShortcuts;

    public MergePageView()
    {
        InitializeComponent();
        _shortcuts = new ShortcutRouter(Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control)
            .Map(Key.Up, KeyModifiers.Alt, () => Page?.MoveUpCommand, ClickSoundKind.Plastic)
            .Map(Key.Down, KeyModifiers.Alt, () => Page?.MoveDownCommand, ClickSoundKind.Plastic)
            .Map(Key.Escape, KeyModifiers.None, () => Page?.BackCommand, ClickSoundKind.Plastic);
        AddHandler(KeyDownEvent, LeaveTextEntryOnEscape, RoutingStrategies.Bubble);
        SourceList.ContainerPrepared += SourceRowPrepared;
        Loaded += (_, _) => Focus();
    }

    private MergePageViewModel? Page => DataContext as MergePageViewModel;

    /// <summary>Takes the shortcuts to the window while the page is shown, so they work whatever has focus (a moved
    /// row's old container is gone, so focus can be nowhere). They tunnel, because the list would take Alt+arrows as
    /// plain arrows.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } window) _windowShortcuts = _shortcuts.Attach(window, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _windowShortcuts?.Dispose();
        _windowShortcuts = null;
    }

    /// <summary>The list's selection reaches the view model only from the user's clicks and arrows. A reorder replaces
    /// the moved row's container and the list drops that row's selection; that must not clear the view model's choice,
    /// which the list shows again through <see cref="MergePageViewModel.SelectedIndex"/>.</summary>
    private void SourceSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (Page is { } page && args.AddedItems is [MergeSource added, ..]) page.SelectedSource = added;
    }

    // The moved row's new container takes focus back when the old one took focus with it, so arrows keep working on it.
    private void SourceRowPrepared(object? sender, ContainerPreparedEventArgs args)
    {
        if (Page is { } page && args.Index == page.SelectedIndex && FocusIsNowhere()) args.Container.Focus(NavigationMethod.Directional);
    }

    private bool FocusIsNowhere() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused || focused.GetVisualRoot() is null;

    // Esc in a text field returns focus to the page, so the next Esc (and every shortcut) works again.
    private void LeaveTextEntryOnEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || args.Handled || !ShortcutRouter.IsTextEntry(args.Source)) return;
        args.Handled = true;
        Focus();
    }
}
