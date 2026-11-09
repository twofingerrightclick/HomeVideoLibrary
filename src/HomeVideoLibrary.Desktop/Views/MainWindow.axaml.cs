using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await ViewModel.RestoreLibraryAsync();
        Closing += (_, args) =>
        {
            if (!ViewModel.IsBusy) return;
            ViewModel.CancelImport();
            args.Cancel = true;
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
}
