using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.Desktop.Views;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class PageHostTests
{
    [AvaloniaFact]
    public void Each_page_gets_a_new_view()
    {
        var host = new ContentControl { ContentTemplate = PageViews.Template };
        var window = new Window { Content = host };
        window.Show();

        host.Content = Page("A");
        Dispatcher.UIThread.RunJobs();
        var first = host.GetVisualDescendants().OfType<UserControl>().First();
        host.Content = Page("B");
        Dispatcher.UIThread.RunJobs();
        var second = host.GetVisualDescendants().OfType<UserControl>().First();

        Assert.NotSame(first, second);
    }

    private static MergePageViewModel Page(string name) => new(
        [Video(name + " 1"), Video(name + " 2")], NoClipEditing.Edits(), () => Task.CompletedTask);

    private static LibraryVideo Video(string name) =>
        new(Guid.NewGuid(), "Videos/a.mkv", name, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
