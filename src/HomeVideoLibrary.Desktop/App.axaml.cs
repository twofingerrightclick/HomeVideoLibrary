using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppErrors.InstallDispatcherHandler();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The Recycle Bin's warning dialog, should Windows show one, belongs to the main window.
            nint windowHandle = 0;
            var viewModel = new MainWindowViewModel(new MainWindowDependencies
            {
                Interaction = new WindowInteraction(() => desktop.MainWindow),
                Trash = FileTrash.ForCurrentPlatform(() => windowHandle),
            });
            AppErrors.Reported += viewModel.ShowError;
            var window = new MainWindow { DataContext = viewModel };
            window.Opened += (_, _) => windowHandle = window.TryGetPlatformHandle()?.Handle ?? 0;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => (ClickSounds.Service as IDisposable)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
