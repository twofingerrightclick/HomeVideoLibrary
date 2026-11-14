using Avalonia.Controls;
using Avalonia.Interactivity;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class SettingsWindow : Window
{
    private void ShowAbout(object? sender, RoutedEventArgs args) =>
        AppErrors.Run(() => AboutDialog.ShowAsync(this), "About Home Video Library");

    public SettingsWindow()
    {
        InitializeComponent();
        // Closing its own window is the view's job; the settings themselves apply as they change.
        CloseButton.Command = new RelayCommand(Close);
    }
}
