using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Desktop.Views.Editing;
using HomeVideoLibrary.Desktop.Views.VideoPage;
using HomeVideoLibrary.UI.Controls;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class EditingViewTests
{
    [AvaloniaFact]
    public void Reordering_a_merge_keeps_the_list_selection()
    {
        var page = Merge();
        var (window, list) = Show(page);
        var second = page.Sources[1];
        list.SelectedItem = second;
        Dispatcher.UIThread.RunJobs();

        page.MoveUpCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(second, page.Sources[0]);
        Assert.Same(second, page.SelectedSource);
        Assert.Same(second, list.SelectedItem);
        Assert.Same(second, page.SelectedSource);

        list.ContainerFromIndex(0)!.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(second, page.Sources[1]);
        Assert.Same(second, list.SelectedItem);
    }

    [AvaloniaFact]
    public void Enter_commits_a_typed_in_point()
    {
        var panel = new TrimPanelViewModel(Video("Source", TimeSpan.FromMinutes(10)), NoClipEditing.Edits(), () => TimeSpan.Zero, () => false);
        var window = new Window { Content = new EditTab { DataContext = panel }, Width = 340, Height = 900 };
        window.Show();
        var field = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "InField");

        field.Focus();
        window.KeyTextInput("1:30");
        Assert.Null(panel.In);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimeSpan.FromSeconds(90), panel.In);
        Assert.Equal("0:01:30.000", field.Text);
    }

    [AvaloniaFact]
    public void The_I_and_O_keys_mark_the_range_on_the_scrub_bar_and_open_the_edit_tab()
    {
        var (window, host) = VideoPageFixture.Window();
        var page = VideoPageFixture.Page();
        VideoPageFixture.Show(host, page);
        var view = host.GetVisualDescendants().OfType<VideoPageView>().Single();

        page.Player.Seek(TimeSpan.FromSeconds(12));
        view.Focus();
        window.KeyPressQwerty(PhysicalKey.I, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.I, RawInputModifiers.None);
        page.Player.Seek(TimeSpan.FromSeconds(40));
        window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((int)InspectorTab.Edit, page.SelectedTab);
        var bar = host.GetVisualDescendants().OfType<ScrubBar>().Single();
        Assert.Equal((TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(40)), (bar.RangeStart, bar.RangeEnd));
        var tab = host.GetVisualDescendants().OfType<EditTab>().Single();
        Assert.Same(page.Edit, tab.DataContext);
    }

    private static (Window Window, ListBox List) Show(MergePageViewModel page)
    {
        var window = new Window { Content = new MergePageView { DataContext = page }, Width = 1000, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<ListBox>().Single());
    }

    private static MergePageViewModel Merge() =>
        new([Video("Christmas '91", TimeSpan.FromMinutes(42)), Video("Lake Tahoe", TimeSpan.FromMinutes(12)), Video("Little league", TimeSpan.FromMinutes(7))],
            NoClipEditing.Edits(), () => Task.CompletedTask);

    private static LibraryVideo Video(string name, TimeSpan duration) =>
        new(Guid.NewGuid(), $"Videos/{name}.mkv", name, duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
