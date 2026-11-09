using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

public sealed class ChapterListItemBehaviorTests
{
    [AvaloniaFact]
    public void Enter_commits_the_trimmed_draft_exactly_once_and_ends_the_edit()
    {
        var commit = new RecordingCommand();
        var (window, row) = Host(new ChapterListItem { Title = "Cake", CommitTitleCommand = commit });
        row.IsEditing = true;
        Dispatcher.UIThread.RunJobs();
        Editor(row).SelectAll();

        window.KeyTextInput("  Candles ");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Candles"], commit.Calls);
        Assert.False(row.IsEditing);
        Assert.Null(row.DraftTitle);
        Assert.Null(EditorOrNull(row));
    }

    [AvaloniaFact]
    public void Escape_discards_the_draft()
    {
        var commit = new RecordingCommand();
        var (window, row) = Host(new ChapterListItem { Title = "Cake", CommitTitleCommand = commit });
        row.IsEditing = true;
        Dispatcher.UIThread.RunJobs();

        window.KeyTextInput("x");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Empty(commit.Calls);
        Assert.False(row.IsEditing);
    }

    [AvaloniaFact]
    public void Each_edit_gets_a_fresh_field_so_undo_cannot_reach_an_earlier_edit()
    {
        var (window, row) = Host(new ChapterListItem { Title = "Cake" });
        row.IsEditing = true;
        Dispatcher.UIThread.RunJobs();
        var first = Editor(row);
        window.KeyTextInput("typed in the first edit");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        row.IsEditing = true;
        Dispatcher.UIThread.RunJobs();
        var second = Editor(row);

        second.Undo();
        second.Undo();

        Assert.NotSame(first, second);
        Assert.NotEqual("typed in the first edit", second.Text);
    }

    private static TextBox Editor(ChapterListItem row) => EditorOrNull(row) ?? throw new InvalidOperationException("No title field.");

    private static TextBox? EditorOrNull(ChapterListItem row) => row.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
}
