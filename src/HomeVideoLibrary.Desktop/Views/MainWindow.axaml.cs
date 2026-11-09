using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using Avalonia;
using System.Text.Json;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private static readonly DataFormat<string> VideoIdsFormat = DataFormat.CreateStringApplicationFormat("HomeVideoLibrary.VideoIds");
    private Point? _dragStart;
    private bool _dragging;
    private TapeViewModel? _pressedTape;
    private Guid[] _dragVideoIds = [];
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.RestoreLibraryAsync();
        AddHandler(KeyDownEvent, MarkerKeyDown, RoutingStrategies.Bubble);
        VideoList.SizeChanged += (_, _) => { ViewModel.SetTapeColumns(VideoList.Bounds.Width >= 1000 ? 2 : 1); UpdateScrollDate(); };
        VideoList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => UpdateScrollDate());
        VideoList.AddHandler(PointerPressedEvent, (_, args) =>
        {
            var source = args.Source as Control;
            var tapeView = source as TapeView ?? source?.FindAncestorOfType<TapeView>();
            if (source is CheckBox || source?.FindAncestorOfType<CheckBox>() is not null) return;
            if (!args.GetCurrentPoint(VideoList).Properties.IsLeftButtonPressed || tapeView?.DataContext is not TapeViewModel tape) return;
            _dragStart = args.GetPosition(VideoList);
            _pressedTape = tape;
            args.Pointer.Capture(this);
            _dragVideoIds = tape.IsSelected ? ViewModel.SelectedVideos.Select(selected => selected.Id).ToArray() : [tape.Video.Id];
        }, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, VideoPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, async (_, args) =>
        {
            var pressed = _pressedTape; _pressedTape = null;
            var start = _dragStart; _dragStart = null;
            var wasDragging = _dragging;
            _dragging = false; ViewModel.IsDraggingTapes = false;
            args.Pointer.Capture(null);
            if (pressed is null || start is null) return;
            var delta = args.GetPosition(VideoList) - start.Value;
            if (wasDragging || Math.Abs(delta.X) + Math.Abs(delta.Y) >= 8)
            {
                var target = this.InputHitTest(args.GetPosition(this)) as Control;
                var album = target?.DataContext as VideoAlbum
                    ?? target?.GetVisualAncestors().OfType<Control>().Select(control => control.DataContext).OfType<VideoAlbum>().FirstOrDefault();
                if (album is null && (target == LibraryDrawer || target?.GetVisualAncestors().Contains(LibraryDrawer) == true))
                    album = ViewModel.CurrentAlbum ?? ViewModel.TargetAlbum;
                if (album is not null) await ViewModel.AddToAlbumAsync(album.Id, _dragVideoIds);
                return;
            }
            if (args.KeyModifiers != KeyModifiers.None) ViewModel.ToggleTapeSelection(pressed);
            else await ViewModel.OpenVideoAsync(pressed.Video);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        Closing += async (_, args) =>
        {
            if (_allowClose) return;
            if (ViewModel.IsBusy) { ViewModel.CancelImport(); args.Cancel = true; return; }
            if (!ViewModel.HasEditor) return;
            args.Cancel = true;
            if (ViewModel.Editor?.IsBusy == true || ViewModel.ClipEditor?.IsBusy == true) return;
            CloseWindowIfRequested(await ViewModel.RequestCloseEditorAsync(closeWindow: true));
        };
        Closed += (_, _) => ViewModel.CancelIndex();
        DataContextChanged += (_, _) => { if (DataContext is MainWindowViewModel vm) vm.TapeRows.CollectionChanged += (_, _) => UpdateScrollDate(); };
    }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    private async void ChooseLibraryClicked(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose your video library folder", AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await ViewModel.OpenLibraryAsync(path);
    }

    private async void ImportClicked(object? sender, RoutedEventArgs args)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import videos into your library", AllowMultiple = true });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
            await ViewModel.ImportAsync(paths);
    }

    private void CancelClicked(object? sender, RoutedEventArgs args) => ViewModel.CancelImport();
    private void ShowLibraryClicked(object? sender, RoutedEventArgs args) => ViewModel.ShowLibrary();
    private async void CreateAlbumClicked(object? sender, RoutedEventArgs args) => await ViewModel.CreateAlbumAsync();
    private async void RenameAlbumClicked(object? sender, RoutedEventArgs args) => await ViewModel.RenameAlbumAsync();
    private async void DeleteAlbumClicked(object? sender, RoutedEventArgs args) => await ViewModel.DeleteAlbumAsync();
    private async void RefreshIndexClicked(object? sender, RoutedEventArgs args) => await ViewModel.RefreshIndexAsync();
    private async void RebuildIndexClicked(object? sender, RoutedEventArgs args) => await ViewModel.RefreshIndexAsync(force: true);
    private void CancelIndexClicked(object? sender, RoutedEventArgs args) => ViewModel.CancelIndex();
    private async void AddSelectedClicked(object? sender, RoutedEventArgs args) => await ViewModel.AddSelectedToAlbumAsync();
    private async void AddSelectedHereClicked(object? sender, RoutedEventArgs args) => await ViewModel.AddSelectedToCurrentAsync();
    private async void RemoveSelectedClicked(object? sender, RoutedEventArgs args) => await ViewModel.RemoveSelectedFromCurrentAsync();
    private void ToggleAlbumAddClicked(object? sender, RoutedEventArgs args) => ViewModel.ToggleAlbumAddMode();
    private void UpdateScrollDate()
    {
        var scroll = VideoList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var index = (int)((scroll?.Offset.Y ?? 0) / 72);
        ScrollDate.Text = ViewModel.TapeRows.Count > 0 ? ViewModel.TapeRows[Math.Clamp(index, 0, ViewModel.TapeRows.Count - 1)].Left.Video.RecordedAt.ToString("MMMM yyyy") : "Your collection";
    }

    private void VideoPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragging || _dragStart is not { } start || _dragVideoIds.Length == 0) return;
        var delta = args.GetPosition(VideoList) - start;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8) return;
        _dragging = true;
        ViewModel.IsDraggingTapes = true;
    }

    private void AlbumDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = args.DataTransfer.Contains(VideoIdsFormat) && !ViewModel.HasEditor ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void AlbumDrop(object? sender, DragEventArgs args)
    {
        var target = (args.Source as Control)?.DataContext as VideoAlbum ?? ViewModel.CurrentAlbum ?? ViewModel.TargetAlbum;
        var value = args.DataTransfer.TryGetValue(VideoIdsFormat);
        if (target is null || value is null) return;
        try
        {
            var payload = JsonSerializer.Deserialize<VideoDragPayload>(value);
            if (payload is null || payload.Ids is null || payload.LibraryRoot != ViewModel.LibraryRoot) return;
            args.Handled = true;
            await ViewModel.AddToAlbumAsync(target.Id, payload.Ids);
        }
        catch (JsonException) { }
    }

    private sealed record VideoDragPayload(string LibraryRoot, Guid[] Ids);
    private async void VideoListKeyDown(object? sender, KeyEventArgs args)
    {
        var focused = FocusManager?.GetFocusedElement() as Control;
        var tape = (focused as TapeView ?? focused?.FindAncestorOfType<TapeView>())?.DataContext as TapeViewModel
            ?? (VideoList.SelectedItem as TapeRow)?.Left;
        if (tape is null || focused is CheckBox) return;
        if (args.Key == Key.Space) { ViewModel.ToggleTapeSelection(tape); args.Handled = true; }
        if (args.Key == Key.Enter) { args.Handled = true; await ViewModel.OpenVideoAsync(tape.Video); }
    }
    private async void OpenSelectedClicked(object? sender, RoutedEventArgs args) => await ViewModel.OpenSelectedAsync();
    private async void EditClipsClicked(object? sender, RoutedEventArgs args) => await ViewModel.OpenClipEditorAsync();
    private async void BackClicked(object? sender, RoutedEventArgs args) => await ViewModel.RequestCloseEditorAsync();
    private async void DiscardClicked(object? sender, RoutedEventArgs args)
    {
        CloseWindowIfRequested(await ViewModel.ConfirmCloseEditorAsync(discard: true));
    }
    private async void SaveAndLeaveClicked(object? sender, RoutedEventArgs args)
    {
        if (ViewModel.Editor is { } editor) await editor.SaveAsync();
        if (ViewModel.Editor?.IsDirty == false) CloseWindowIfRequested(await ViewModel.ConfirmCloseEditorAsync(discard: false));
    }
    private void KeepEditingClicked(object? sender, RoutedEventArgs args) => ViewModel.KeepEditing();
    private void CloseWindowIfRequested(EditorExitDestination destination)
    {
        if (destination != EditorExitDestination.Window) return;
        _allowClose = true;
        Close();
    }
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
