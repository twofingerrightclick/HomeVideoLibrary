using Avalonia.Controls;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Closing its own window is the view's job; the settings themselves apply as they change.
        CloseButton.Command = new RelayCommand(Close);
    }
}
