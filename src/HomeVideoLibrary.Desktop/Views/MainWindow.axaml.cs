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
    private Guid[] _dragVideoIds = [];
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.RestoreLibraryAsync();
        AddHandler(KeyDownEvent, MarkerKeyDown, RoutingStrategies.Bubble);
        VideoList.AddHandler(PointerPressedEvent, (_, args) =>
        {
            var source = args.Source as Control;
            var item = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
            if (!args.GetCurrentPoint(VideoList).Properties.IsLeftButtonPressed || item?.DataContext is not LibraryVideo video) return;
            _dragStart = args.GetPosition(VideoList);
            // Capture an existing multi-selection before ListBox's press handler can collapse it.
            _dragVideoIds = ViewModel.SelectedVideos.Any(selected => selected.Id == video.Id)
                ? ViewModel.SelectedVideos.Select(selected => selected.Id).ToArray() : [video.Id];
        }, RoutingStrategies.Tunnel);
        VideoList.AddHandler(PointerMovedEvent, VideoPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        VideoList.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Bubble, handledEventsToo: true);
        Closing += async (_, args) =>
        {
            if (_allowClose) return;
            if (ViewModel.IsBusy) { ViewModel.CancelImport(); args.Cancel = true; return; }
            if (ViewModel.Editor is null) return;
            args.Cancel = true;
            if (ViewModel.Editor.IsBusy) return;
            CloseWindowIfRequested(await ViewModel.RequestCloseEditorAsync(closeWindow: true));
        };
        Closed += (_, _) => ViewModel.CancelIndex();
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
    private void VideoSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (DataContext is MainWindowViewModel vm && sender is ListBox list) vm.SetSelectedVideos(list.SelectedItems?.OfType<LibraryVideo>() ?? []);
    }

    private async void VideoPointerMoved(object? sender, PointerEventArgs args)
    {
        if (_dragging || _dragStart is not { } start || !args.GetCurrentPoint(VideoList).Properties.IsLeftButtonPressed || _dragVideoIds.Length == 0) return;
        var delta = args.GetPosition(VideoList) - start;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 8) return;
        _dragging = true; _dragStart = null;
        try
        {
            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(VideoIdsFormat, JsonSerializer.Serialize(new VideoDragPayload(ViewModel.LibraryRoot, _dragVideoIds))));
            await DragDrop.DoDragDropAsync(args, data, DragDropEffects.Copy);
        }
        finally { _dragging = false; }
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
    private async void VideoDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is ListBox { SelectedItem: LibraryVideo video }) await ViewModel.OpenVideoAsync(video);
    }
    private async void VideoListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Enter && sender is ListBox { SelectedItem: LibraryVideo video })
        {
            args.Handled = true;
            await ViewModel.OpenVideoAsync(video);
        }
    }
    private async void OpenSelectedClicked(object? sender, RoutedEventArgs args) => await ViewModel.OpenSelectedAsync();
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
