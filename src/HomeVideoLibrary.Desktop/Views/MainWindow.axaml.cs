using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private const double DragThreshold = 8;
    private static readonly DataFormat<string> VideoIdsFormat = DataFormat.CreateStringApplicationFormat("HomeVideoLibrary.VideoIds");
    private static readonly FilePickerFileType VideoFiles = new("Videos")
    {
        Patterns = ["*.mp4", "*.m4v", "*.mov", "*.mkv", "*.avi", "*.mts", "*.m2ts", "*.ts", "*.mpg", "*.mpeg", "*.vob", "*.wmv", "*.3gp", "*.webm", "*.dv", "*.flv"],
        MimeTypes = ["video/*"],
        AppleUniformTypeIdentifiers = ["public.movie"],
    };
    private bool _allowClose, _closing;
    private TapeViewModel? _pressedTape;
    private Point _pressPoint;
    private bool _dragging;
    private ListBoxItem? _dropTarget;

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => AppErrors.Run(() => ViewModel.RestoreLibraryAsync(), "Restore library");
        Activated += (_, _) => AppErrors.Run(() => ViewModel.WindowActivatedAsync(), "Refresh on activation");
        AddHandler(KeyDownEvent, MarkerKeyDown, RoutingStrategies.Bubble);
        VideoList.SizeChanged += (_, args) =>
        {
            // A hidden list reports width 0; keep the last column count rather than flipping to one column.
            if (args.NewSize.Width <= 0) return;
            ViewModel.SetTapeColumns(args.NewSize.Width >= 1000 ? 2 : 1); UpdateScrollDate();
        };
        VideoList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => UpdateScrollDate());
        VideoList.AddHandler(PointerPressedEvent, TapePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        VideoList.AddHandler(PointerMovedEvent, TapePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        VideoList.AddHandler(PointerReleasedEvent, TapeReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Closing += WindowClosing;
        Closed += (_, _) => ViewModel.CancelIndex();
        DataContextChanged += (_, _) => { if (DataContext is MainWindowViewModel vm) vm.TapeRows.CollectionChanged += (_, _) => UpdateScrollDate(); };
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private void WindowClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (ViewModel.IsImporting)
        {
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            AppErrors.Run(async () =>
            {
                await ViewModel.CancelImportAndWaitAsync();
                _allowClose = true;
                Close();
            }, "Close window");
            return;
        }
        if (!ViewModel.HasEditor) return;
        args.Cancel = true;
        AppErrors.Run(async () =>
        {
            if (!await ViewModel.CloseEditorAsync()) return;
            _allowClose = true;
            Close();
        }, "Close window");
    }

    private static TapeViewModel? TapeAt(object? source)
    {
        var control = source as Control;
        if (control is CheckBox || control?.FindAncestorOfType<CheckBox>() is not null) return null;
        return (control as TapeView ?? control?.FindAncestorOfType<TapeView>())?.DataContext as TapeViewModel;
    }

    private void TapePressed(object? sender, PointerPressedEventArgs args)
    {
        _pressedTape = null;
        if (!args.GetCurrentPoint(VideoList).Properties.IsLeftButtonPressed || TapeAt(args.Source) is not { } tape) return;
        _pressedTape = tape;
        _pressPoint = args.GetPosition(VideoList);
    }

    private async void TapePointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragging || _pressedTape is not { } tape || !args.GetCurrentPoint(VideoList).Properties.IsLeftButtonPressed) return;
        var delta = args.GetPosition(VideoList) - _pressPoint;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < DragThreshold) return;
        _dragging = true;
        ViewModel.IsDraggingTapes = true;
        try
        {
            var payload = JsonSerializer.Serialize(new VideoDragPayload(ViewModel.LibraryRoot, [.. ViewModel.DragVideoIds(tape)]));
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
        // Act only when the release lands on the tape that was pressed, so a stale press can never fire elsewhere.
        if (_dragging || pressed is null || args.InitialPressMouseButton != MouseButton.Left || TapeAt(VideoList.InputHitTest(args.GetPosition(VideoList))) != pressed)
            return;
        var command = TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) ViewModel.SelectRange(pressed);
        else if (args.KeyModifiers.HasFlag(command) || args.KeyModifiers.HasFlag(KeyModifiers.Control)) ViewModel.ToggleTapeSelection(pressed);
        else if (args.KeyModifiers == KeyModifiers.None) AppErrors.Run(() => ViewModel.OpenVideoAsync(pressed.Video), "Open video");
    }

    private void AlbumDragOver(object? sender, DragEventArgs args)
    {
        var target = AlbumItemAt(args.Source);
        var accepted = target is not null && args.DataTransfer.Contains(VideoIdsFormat) && !ViewModel.HasEditor;
        args.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropTarget(accepted ? target : null);
        args.Handled = true;
    }

    private void AlbumDragLeave(object? sender, DragEventArgs args) => SetDropTarget(null);

    private void AlbumDrop(object? sender, DragEventArgs args)
    {
        SetDropTarget(null);
        args.Handled = true;
        if (AlbumItemAt(args.Source)?.DataContext is not VideoAlbum album || args.DataTransfer.TryGetValue(VideoIdsFormat) is not { } value) return;
        VideoDragPayload? payload;
        try { payload = JsonSerializer.Deserialize<VideoDragPayload>(value); }
        catch (JsonException) { return; }
        if (payload?.Ids is not { Length: > 0 } ids || payload.LibraryRoot != ViewModel.LibraryRoot) return;
        args.DragEffects = DragDropEffects.Copy;
        AppErrors.Run(() => ViewModel.AddToAlbumAsync(album.Id, ids), "Add to album");
    }

    private static ListBoxItem? AlbumItemAt(object? source)
    {
        var control = source as Control;
        var item = control as ListBoxItem ?? control?.FindAncestorOfType<ListBoxItem>();
        return item?.DataContext is VideoAlbum ? item : null;
    }

    private void SetDropTarget(ListBoxItem? item)
    {
        if (ReferenceEquals(item, _dropTarget)) return;
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = item;
        _dropTarget?.Classes.Add("dropTarget");
    }

    private sealed record VideoDragPayload(string LibraryRoot, Guid[] Ids);

    private void AlbumSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems is [VideoAlbum album]) ViewModel.ShowAlbum(album);
    }

    private void TargetAlbumSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems is [VideoAlbum album]) ViewModel.ChooseTargetAlbum(album);
    }

    private void ChooseLibraryClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(async () =>
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your video library folder", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await ViewModel.OpenLibraryAsync(path);
    }, "Choose library");

    private void ImportClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import videos into your library", AllowMultiple = true, FileTypeFilter = [VideoFiles, FilePickerFileTypes.All],
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        await ViewModel.ImportAsync(paths, unavailablePicks: files.Count - paths.Length);
    }, "Import");

    private void CancelClicked(object? sender, RoutedEventArgs args) => ViewModel.CancelImport();
    private void ShowLibraryClicked(object? sender, RoutedEventArgs args) => ViewModel.ShowLibrary();
    private void CreateAlbumClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.CreateAlbumAsync, "Create album");
    private void RenameAlbumClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.RenameAlbumAsync, "Rename album");
    private void DeleteAlbumClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.DeleteAlbumAsync, "Delete album");
    private void RefreshIndexClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(() => ViewModel.RefreshIndexAsync(), "Refresh library");
    private void RebuildIndexClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(() => ViewModel.RefreshIndexAsync(force: true), "Rebuild library");
    private void CancelIndexClicked(object? sender, RoutedEventArgs args) => ViewModel.CancelIndex();
    private void AddSelectedClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.AddSelectedToAlbumAsync, "Add to album");
    private void AddSelectedHereClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.AddSelectedToCurrentAsync, "Add to album");
    private void RemoveSelectedClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.RemoveSelectedFromCurrentAsync, "Remove from album");
    private void ToggleAlbumAddClicked(object? sender, RoutedEventArgs args) => ViewModel.ToggleAlbumAddMode();

    private void UpdateScrollDate()
    {
        var scroll = VideoList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var index = (int)((scroll?.Offset.Y ?? 0) / 72);
        ScrollDate.Text = ViewModel.TapeRows.Count > 0 ? ViewModel.TapeRows[Math.Clamp(index, 0, ViewModel.TapeRows.Count - 1)].Left.MonthLabel : "Your collection";
    }

    private void VideoListKeyDown(object? sender, KeyEventArgs args)
    {
        var command = TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        if (args.Key == Key.A && args.KeyModifiers == command) { ViewModel.SelectAllVisible(); args.Handled = true; return; }
        if (args.Key == Key.Escape) { ViewModel.ClearSelection(); args.Handled = true; return; }
        var focused = FocusManager?.GetFocusedElement() as Control;
        var tape = (focused as TapeView ?? focused?.FindAncestorOfType<TapeView>())?.DataContext as TapeViewModel
            ?? (VideoList.SelectedItem as TapeRow)?.Left;
        if (tape is null || focused is CheckBox) return;
        if (args.Key == Key.Space)
        {
            if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) ViewModel.SelectRange(tape); else ViewModel.ToggleTapeSelection(tape);
            args.Handled = true;
        }
        if (args.Key == Key.Enter) { args.Handled = true; AppErrors.Run(() => ViewModel.OpenVideoAsync(tape.Video), "Open video"); }
    }

    private void OpenSelectedClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.OpenSelectedAsync, "Open video");
    private void EditClipsClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.OpenClipEditorAsync, "Edit clips");
    private void BackClicked(object? sender, RoutedEventArgs args) => AppErrors.Run(ViewModel.CloseEditorAsync, "Back to library");
    private void SaveAndLeaveClicked(object? sender, RoutedEventArgs args) => ViewModel.LeavePrompt?.Answer(LeaveChoice.Save);
    private void DiscardClicked(object? sender, RoutedEventArgs args) => ViewModel.LeavePrompt?.Answer(LeaveChoice.Discard);
    private void KeepEditingClicked(object? sender, RoutedEventArgs args) => ViewModel.LeavePrompt?.Answer(LeaveChoice.KeepEditing);

    private void MarkerKeyDown(object? sender, KeyEventArgs args)
    {
        if (ViewModel.Editor is not { } editor) return;
        var focused = FocusManager?.GetFocusedElement() as Control;
        var textInput = focused is TextBox || focused?.FindAncestorOfType<TextBox>() is not null;
        if (!VideoEditorViewModel.ShouldAddMarker(args.Key == Key.M, args.KeyModifiers != KeyModifiers.None, textInput, editor.IsBusy)) return;
        editor.AddChapter();
        args.Handled = true;
    }
}
