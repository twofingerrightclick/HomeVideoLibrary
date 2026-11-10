using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>The main window. Code here is limited to what views own: keyboard routing, focus, the sticky month's
/// viewport measurement, and tape drag-and-drop onto albums.</summary>
public sealed partial class MainWindow : Window
{
    private const double DragThreshold = 8;
    private static readonly DataFormat<string> VideoIdsFormat = DataFormat.CreateStringApplicationFormat("HomeVideoLibrary.VideoIds");
    private TapeViewModel? _pressedTape;
    private Point _pressPoint;
    private bool _dragging;
    private ListBoxItem? _dropTarget;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => AppErrors.Run(() => ViewModel.RestoreLibraryAsync(), "Restore library");
        Activated += (_, _) => AppErrors.Run(() => ViewModel.WindowActivatedAsync(), "Refresh on activation");
        Closing += WindowClosing;
        DataContextChanged += (_, _) => Subscribe(DataContext as MainWindowViewModel);

        var router = new ShortcutRouter(ShortcutRouter.CommandModifierOf(this));
        SearchHint.Text = router.CommandModifier == KeyModifiers.Meta ? "⌘F" : "Ctrl+F";
        MapShortcuts(router);
        router.Attach(this);

        TapeList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => UpdateTopVisible());
        TapeList.AddHandler(PointerPressedEvent, TapePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TapeList.AddHandler(PointerMovedEvent, TapePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        TapeList.AddHandler(PointerReleasedEvent, TapeReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TapeList.AddHandler(PointerCaptureLostEvent, (_, _) => { if (!_dragging) _pressedTape = null; }, RoutingStrategies.Bubble, handledEventsToo: true);
        AlbumList.AddHandler(DragDrop.DragOverEvent, AlbumDragOver);
        AlbumList.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropTarget(null));
        AlbumList.AddHandler(DragDrop.DropEvent, AlbumDrop);
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private MainWindowViewModel? _subscribed;

    private void Subscribe(MainWindowViewModel? viewModel)
    {
        if (_subscribed is { } old) old.SearchFocusRequested -= FocusSearch;
        _subscribed = viewModel;
        if (viewModel is not null) viewModel.SearchFocusRequested += FocusSearch;
    }

    private void MapShortcuts(ShortcutRouter router)
    {
        Func<System.Windows.Input.ICommand?> OnShelf(Func<System.Windows.Input.ICommand?> command) => () => ViewModel.IsShelfShown ? command() : null;
        router
            .MapCommand(Key.F, () => ViewModel.FocusSearchCommand)
            .MapCommand(Key.I, () => ViewModel.Import.ImportCommand, ClickSoundKind.Plastic)
            .MapCommand(Key.OemComma, () => ViewModel.OpenSettingsCommand, ClickSoundKind.Plastic)
            .Map(Key.Enter, KeyModifiers.None, OnShelf(() => FocusedTape() is { } tape ? tape.HasMatchedChapter ? tape.OpenAtMatchCommand : tape.OpenCommand : null))
            .Map(Key.F2, KeyModifiers.None, OnShelf(() => FocusedAlbum()?.RenameCommand))
            .Map(Key.Space, KeyModifiers.None, OnShelf(() => FocusedTape()?.ToggleSelectionCommand))
            .Map(Key.Space, KeyModifiers.Shift, OnShelf(() => FocusedTape()?.SelectRangeCommand))
            .Map(Key.Escape, KeyModifiers.None, OnShelf(() => ViewModel.Shelf.ClearCommand))
            .Map(Key.Escape, KeyModifiers.None, () => ViewModel.ShowsShellBackKey ? ViewModel.BackCommand : null)
            .Map(Key.Delete, KeyModifiers.None, OnShelf(() => ViewModel.DeleteSelectedCommand))
            .Map(Key.Back, KeyModifiers.None, OnShelf(() => ViewModel.DeleteSelectedCommand));
        router
            .MapCommand(Key.A, OnShelf(() => ViewModel.Shelf.SelectAllCommand))
            .MapCommand(Key.N, OnShelf(() => ViewModel.Albums.CreateFromSelectionCommand), ClickSoundKind.Plastic)
            .MapCommand(Key.M, OnShelf(() => ViewModel.MergeSelectedCommand), ClickSoundKind.Plastic, extra: KeyModifiers.Shift);
    }

    /// <summary>The album whose row has keyboard focus.</summary>
    private AlbumRowViewModel? FocusedAlbum() =>
        (FocusManager?.GetFocusedElement() as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as AlbumRowViewModel;

    /// <summary>The tape whose row has keyboard focus.</summary>
    private TapeViewModel? FocusedTape() =>
        (FocusManager?.GetFocusedElement() as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TapeViewModel;

    private void FocusSearch()
    {
        SearchBox.Focus(NavigationMethod.Tab);
        SearchBox.SelectAll();
    }

    /// <summary>Every close goes through the view model's awaited close flow (import, unsaved changes, running edits);
    /// once it agrees, this handler steps aside and the window closes for real.</summary>
    private void WindowClosing(object? sender, WindowClosingEventArgs args)
    {
        args.Cancel = true;
        AppErrors.Run(async () =>
        {
            if (!await ViewModel.RequestCloseAsync()) return;
            Closing -= WindowClosing;
            Close();
        }, "Close window");
    }

    /// <summary>Tells the shelf which tape is at the top of the viewport (for the sticky month pill).</summary>
    private void UpdateTopVisible()
    {
        TapeViewModel? top = null;
        var topIndex = int.MaxValue;
        foreach (var container in TapeList.GetRealizedContainers())
        {
            if (container.TranslatePoint(new Point(0, container.Bounds.Height), TapeList) is not { Y: > 0 }) continue;
            var index = TapeList.IndexFromContainer(container);
            if (index >= 0 && index < topIndex) { topIndex = index; top = container.DataContext as TapeViewModel; }
        }
        ViewModel.Shelf.SetTopVisible(top);
    }

    private static TapeViewModel? TapeAt(object? source)
    {
        var control = source as Control;
        if (control is CheckBox || control?.FindAncestorOfType<CheckBox>() is not null) return null;
        return control?.FindAncestorOfType<TapeView>(includeSelf: true)?.DataContext as TapeViewModel;
    }

    // Selection gestures: Shift extends from the anchor, the command key toggles, and a right-click on an unselected
    // tape selects only it so the context menu acts on what was clicked. A plain click opens (the spine's command).
    private void TapePressed(object? sender, PointerPressedEventArgs args)
    {
        _pressedTape = null;
        if (TapeAt(args.Source) is not { } tape) return;
        var point = args.GetCurrentPoint(TapeList);
        if (point.Properties.IsRightButtonPressed)
        {
            if (!tape.IsSelected) { ViewModel.Shelf.ClearSelection(); tape.IsSelected = true; }
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;
        _pressedTape = tape;
        _pressPoint = args.GetPosition(TapeList);
    }

    private void TapePointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragging || _pressedTape is not { } tape || !args.GetCurrentPoint(TapeList).Properties.IsLeftButtonPressed) return;
        var delta = args.GetPosition(TapeList) - _pressPoint;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < DragThreshold) return;
        // The drag must start inside this pointer event; the returned task ends when the drop does.
        _ = DragTapesAsync(args, tape);
    }

    private async Task DragTapesAsync(PointerEventArgs args, TapeViewModel tape)
    {
        _dragging = true;
        ViewModel.IsDraggingTapes = true;
        try
        {
            var payload = JsonSerializer.Serialize(new VideoDragPayload(ViewModel.Settings.LibraryRoot, [.. ViewModel.Shelf.DragVideoIds(tape)]));
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(VideoIdsFormat, payload));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Copy);
        }
        catch (Exception exception) { AppErrors.Report(exception, "Drag tapes"); }
        finally
        {
            _dragging = false; _pressedTape = null;
            ViewModel.IsDraggingTapes = false;
            SetDropTarget(null);
        }
    }

    private void TapeReleased(object? sender, PointerReleasedEventArgs args)
    {
        var pressed = _pressedTape;
        _pressedTape = null;
        if (_dragging || pressed is null || args.InitialPressMouseButton != MouseButton.Left || TapeAt(args.Source) != pressed) return;
        var command = ShortcutRouter.CommandModifierOf(this);
        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) pressed.SelectRangeCommand.Execute(null);
        else if (args.KeyModifiers.HasFlag(command) || args.KeyModifiers.HasFlag(KeyModifiers.Control)) pressed.ToggleSelectionCommand.Execute(null);
        else return;
        args.Handled = true;
    }

    private void AlbumDragOver(object? sender, DragEventArgs args)
    {
        var target = AlbumItemAt(args.Source);
        var accepted = target is not null && args.DataTransfer.Contains(VideoIdsFormat);
        args.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropTarget(accepted ? target : null);
        args.Handled = true;
    }

    private void AlbumDrop(object? sender, DragEventArgs args)
    {
        SetDropTarget(null);
        args.Handled = true;
        if (AlbumItemAt(args.Source)?.DataContext is not AlbumRowViewModel album || args.DataTransfer.TryGetValue(VideoIdsFormat) is not { } value) return;
        VideoDragPayload? payload;
        try { payload = JsonSerializer.Deserialize<VideoDragPayload>(value); }
        catch (JsonException) { return; }
        if (payload?.Ids is not { Length: > 0 } ids || payload.LibraryRoot != ViewModel.Settings.LibraryRoot) return;
        args.DragEffects = DragDropEffects.Copy;
        AppErrors.Run(() => ViewModel.Albums.AddToAlbumAsync(album.Id, ids), "Add to album");
    }

    private static ListBoxItem? AlbumItemAt(object? source)
    {
        var item = (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        return item?.DataContext is AlbumRowViewModel ? item : null;
    }

    private void SetDropTarget(ListBoxItem? item)
    {
        if (ReferenceEquals(item, _dropTarget)) return;
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = item;
        _dropTarget?.Classes.Add("dropTarget");
    }

    private sealed record VideoDragPayload(string LibraryRoot, Guid[] Ids);
}
