using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class AppIdentityTests
{
    [AvaloniaFact]
    public void ApplicationNameIsSetForTheNativeMacMenu()
    {
        var app = new App();
        app.Initialize();
        Assert.Equal("Home Video Library", app.Name);
        var menu = NativeMenu.GetMenu(app)!;
        Assert.Contains(menu.Items.OfType<NativeMenuItem>(), item => item.Header == "About Home Video Library…");
    }

    [AvaloniaFact]
    public void AboutShowsTheDesktopAssemblyVersionAndBuild()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvl-about-" + Guid.NewGuid().ToString("N"));
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = new NoInteraction(),
            Settings = new LibrarySettings(Path.Combine(root, "settings.json")),
        });
        var owner = new MainWindow { DataContext = viewModel };
        try
        {
            owner.Show();
            var answer = owner.ShowAboutAsync();
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            var expectedBuild = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Assert.Equal("Home Video Library", AppInfo.Name);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), block =>
                block.Text == $"Home Video Library\nVersion {expectedBuild.Split('+')[0]}\nBuild {expectedBuild}");
            dialog.Close(true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(answer.IsCompletedSuccessfully);
        }
        finally
        {
            owner.Close();
            Dispatcher.UIThread.RunJobs();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
