using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Gallery.Demo;
using HomeVideoLibrary.Gallery.Sections;
using HomeVideoLibrary.UI.Controls;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

/// <summary>The gallery's reference video page is what the app overhaul copies; these pin its structure.</summary>
public sealed class ReferencePageTests
{
    [AvaloniaFact]
    public void A_title_edit_survives_playhead_updates_with_the_same_field()
    {
        var page = ChaptersSection.CreateVideoA();
        var (window, host) = Host(new ContentControl { ContentTemplate = VideoPageViewModel.ViewTemplate, Content = page });
        var row = page.Chapters.Rows.OfType<ChapterRowViewModel>().First(r => r.Title == "Hot cocoa");
        row.IsEditing = true;
        Dispatcher.UIThread.RunJobs();
        var editor = host.GetVisualDescendants().OfType<ChapterListItem>().Single(i => i.IsEditing)
            .GetVisualDescendants().OfType<TextBox>().Single();
        editor.SelectAll();
        window.KeyTextInput("Cocoa and marshmallows");

        for (var i = 0; i < 20; i++)
        {
            page.Chapters.Position += TimeSpan.FromSeconds(30);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(editor.IsAttachedToVisualTree());
        Assert.Equal("Cocoa and marshmallows", editor.Text);
        Assert.Equal("Cocoa and marshmallows", row.DraftTitle);
    }

    [AvaloniaFact]
    public void Clicking_a_row_expands_it_in_place_one_row_at_a_time()
    {
        var page = ChaptersSection.CreateVideoA();
        var (window, host) = Host(new ContentControl { ContentTemplate = VideoPageViewModel.ViewTemplate, Content = page });
        window.Height = 900;
        Dispatcher.UIThread.RunJobs();
        ChapterListItem Item(string title) => host.GetVisualDescendants().OfType<ChapterListItem>().Single(i => i.Title == title);

        window.MouseDown(At(Item("Hot cocoa"), 0.3), Avalonia.Input.MouseButton.Left);
        window.MouseUp(At(Item("Hot cocoa"), 0.3), Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Item("Hot cocoa").IsExpanded);
        Assert.Single(Item("Hot cocoa").GetVisualDescendants().OfType<TextBox>());

        window.MouseDown(At(Item("Goodnight"), 0.3), Avalonia.Input.MouseButton.Left);
        window.MouseUp(At(Item("Goodnight"), 0.3), Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(Item("Hot cocoa").IsExpanded);
        Assert.Empty(Item("Hot cocoa").GetVisualDescendants().OfType<TextBox>());
        Assert.True(Item("Goodnight").IsExpanded);
    }

    [AvaloniaFact]
    public void The_placeholder_moves_between_chapters_instead_of_rows_being_rebuilt()
    {
        var panel = ChaptersSection.CreateVideoA().Chapters;
        var rowsBefore = panel.Rows.OfType<ChapterRowViewModel>().ToList();
        var resets = 0;
        panel.Rows.CollectionChanged += (_, e) => resets += e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? 1 : 0;

        panel.Position = TimeSpan.FromSeconds(1100);

        Assert.Equal(0, resets);
        Assert.Equal(rowsBefore, panel.Rows.OfType<ChapterRowViewModel>());
        var placeholderIndex = panel.Rows.IndexOf(panel.Rows.OfType<PlaceholderRowViewModel>().Single());
        Assert.Equal("Hot cocoa", ((ChapterRowViewModel)panel.Rows[placeholderIndex - 1]).Title);
    }

    [AvaloniaFact]
    public void Switching_videos_builds_a_new_view_and_leaves_each_draft_with_its_video()
    {
        var a = ChaptersSection.CreateVideoA();
        var b = ChaptersSection.CreateVideoB();
        var (_, host) = Host(new ContentControl { ContentTemplate = VideoPageViewModel.ViewTemplate, Content = a });
        var viewA = host.GetVisualDescendants().OfType<VideoPageView>().Single();
        a.Tags.Draft = "half-typed";

        host.Content = b;
        Dispatcher.UIThread.RunJobs();

        Assert.NotSame(viewA, host.GetVisualDescendants().OfType<VideoPageView>().Single());
        Assert.Null(b.Tags.Draft);
        Assert.Equal("half-typed", a.Tags.Draft);
    }
}
