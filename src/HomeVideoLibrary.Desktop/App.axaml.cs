using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;

namespace HomeVideoLibrary.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppErrors.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            AppErrors.Reported += viewModel.ShowError;
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
