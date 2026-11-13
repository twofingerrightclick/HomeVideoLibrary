using Avalonia.Controls;
using Avalonia.Interactivity;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

public sealed partial class TransportDeck : UserControl
{
    public TransportDeck() => InitializeComponent();

    private void SpeedMenuOpened(object? sender, EventArgs args) => (DataContext as VideoPageViewModel)?.Fullscreen.HoldMenu(true);
    private void SpeedMenuClosed(object? sender, EventArgs args) => (DataContext as VideoPageViewModel)?.Fullscreen.HoldMenu(false);

    private void ChooseSpeed(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: double speed } && DataContext is VideoPageViewModel page)
        {
            page.Player.Speed = speed;
            SpeedButton.Flyout?.Hide();
        }
    }
}
