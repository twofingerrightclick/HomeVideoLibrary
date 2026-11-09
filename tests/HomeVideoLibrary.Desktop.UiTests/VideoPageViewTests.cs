using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Desktop.Views.VideoPage;
using HomeVideoLibrary.UI.Controls;
using Xunit;
using static HomeVideoLibrary.Desktop.UiTests.VideoPageFixture;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class VideoPageViewTests
{
    private static readonly VideoMetadata TwoChapters = VideoMetadata.Empty with
    {
        Chapters = [new VideoChapter(1, TimeSpan.Zero, "Opening presents", "Wrapping paper everywhere", []),
            new VideoChapter(2, TimeSpan.FromSeconds(30), "Sledding", "", [])],
    };

    [AvaloniaFact]
    public void Undo_in_another_videos_title_cannot_bring_back_the_first_videos_text()
    {
        var (window, host) = Window();
        var first = Page();
        Show(host, first);
        var firstView = PageView(host);
        var title = TitleField(host);
        title.Focus();
        window.KeyTextInput("Christmas");
        Assert.Equal("Christmas", first.Details.Title);

        var second = Page();
        Show(host, second);
        Assert.NotSame(firstView, PageView(host));
        var secondTitle = TitleField(host);
        Assert.NotSame(title, secondTitle);
        secondTitle.Focus();
        secondTitle.Undo();

        Assert.Equal("", second.Details.Title);
        Assert.Equal("Christmas", first.Details.Title);
    }

    [AvaloniaFact]
    public void Each_selected_chapter_gets_a_fresh_details_editor()
    {
        var (_, host) = Window();
        var page = Page(TwoChapters);
        page.SelectedTab = (int)InspectorTab.Chapters;
        Show(host, page);
        var chapters = page.Details.Chapters;

        chapters.Chapters[0].JumpCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var firstEditor = DescriptionEditor(host);
        Assert.Equal("Wrapping paper everywhere", firstEditor.Text);
        chapters.Chapters[1].JumpCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var secondEditor = DescriptionEditor(host);

        Assert.NotSame(firstEditor, secondEditor);
        Assert.Equal("", secondEditor.Text);
        Assert.Same(chapters.Chapters[1], chapters.SelectedChapter);
    }

    [AvaloniaFact]
    public void M_types_into_a_text_field_and_adds_a_chapter_from_the_page()
    {
        var (window, host) = Window();
        var page = Page();
        Show(host, page);
        page.Player.Seek(TimeSpan.FromSeconds(12));
        var description = host.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "DescriptionField");

        description.Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        window.KeyTextInput("m");
        window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
        Assert.Equal("m", page.Details.Description);
        Assert.Empty(page.Details.Chapters.Chapters);

        PageView(host).Focus();
        window.KeyPressQwerty(PhysicalKey.M, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.M, RawInputModifiers.None);
        var added = Assert.Single(page.Details.Chapters.Chapters);
        Assert.Equal(TimeSpan.FromSeconds(12), added.Start);
        Assert.True(added.IsEditing);
    }

    [AvaloniaFact]
    public void Inactive_tabs_keep_their_drafts()
    {
        var (window, host) = Window();
        var page = Page();
        Show(host, page);
        TitleField(host).Focus();
        window.KeyTextInput("Lake day");

        page.SelectedTab = (int)InspectorTab.Tags;
        Dispatcher.UIThread.RunJobs();
        var tags = host.GetVisualDescendants().OfType<TagChipInput>().Single();
        tags.Text = "sunset";
        page.SelectedTab = (int)InspectorTab.Info;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Lake day", TitleField(host).Text);
        page.SelectedTab = (int)InspectorTab.Tags;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sunset", host.GetVisualDescendants().OfType<TagChipInput>().Single().Text);
        Assert.Equal("sunset", page.Details.Tags.Draft);
    }

    [AvaloniaFact]
    public void Hovering_the_scrub_bar_reports_the_time_to_the_preview()
    {
        var (window, host) = Window();
        var page = Page(duration: TimeSpan.FromMinutes(10));
        Show(host, page);
        var bar = host.GetVisualDescendants().OfType<ScrubBar>().First(scrub => scrub.IsEffectivelyVisible);
        var middle = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 2), window)!.Value;

        window.MouseMove(middle);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(page.Preview.HoverTime);
        Assert.InRange(page.Preview.HoverTime!.Value.TotalMinutes, 4.5, 5.5);
        window.MouseMove(new Point(2, 2));
        Assert.Null(page.Preview.HoverTime);
    }

    [AvaloniaFact]
    public void Fullscreen_is_applied_to_the_window_and_leaving_the_page_restores_it()
    {
        var (window, host) = Window();
        var page = Page();
        Show(host, page);

        page.Fullscreen.ToggleCommand.Execute(null);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.False(host.GetVisualDescendants().OfType<TabControl>().Single().IsEffectivelyVisible);
        window.WindowState = WindowState.Normal;
        Assert.False(page.Fullscreen.IsFullscreen);

        page.Fullscreen.ToggleCommand.Execute(null);
        host.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    private static VideoPageView PageView(ContentControl host) => host.GetVisualDescendants().OfType<VideoPageView>().Single();

    private static TextBox TitleField(ContentControl host) => host.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "TitleField");

    private static TextBox DescriptionEditor(ContentControl host) =>
        host.GetVisualDescendants().OfType<ChapterDetailsView>().Single().GetVisualDescendants().OfType<TextBox>().First();
}
