using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.RestoreLibraryAsync();
        AddHandler(KeyDownEvent, MarkerKeyDown, RoutingStrategies.Bubble);
        Closing += async (_, args) =>
        {
            if (_allowClose) return;
            if (ViewModel.IsBusy) { ViewModel.CancelImport(); args.Cancel = true; return; }
            if (ViewModel.Editor is null) return;
            args.Cancel = true;
            if (ViewModel.Editor.IsBusy) return;
            CloseWindowIfRequested(await ViewModel.RequestCloseEditorAsync(closeWindow: true));
        };
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
