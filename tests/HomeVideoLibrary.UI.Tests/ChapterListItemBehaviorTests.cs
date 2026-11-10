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

    [AvaloniaFact]
    public void Expanded_content_shows_only_while_expanded_and_its_clicks_are_its_own()
    {
        var jumps = new RecordingCommand();
        var inside = new Border { Height = 60, Background = Avalonia.Media.Brushes.Transparent };
        var (window, row) = Host(new ChapterListItem { Title = "Cake", JumpCommand = jumps, ExpandedContent = inside });
        var expanded = row.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
            .Single(presenter => presenter.Name == ChapterListItem.ExpandedPartName);
        Assert.False(expanded.IsVisible);

        row.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(expanded.IsVisible);
        Assert.True(inside.IsEffectivelyVisible);
        Assert.Contains(":expanded", row.Classes);
        var point = Headless.At(inside, 0.5);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);

        Assert.Empty(jumps.Calls);
    }

    [AvaloniaFact]
    public void A_double_click_is_one_click_and_does_not_rename_a_row_it_expanded()
    {
        var row = new ChapterListItem { Title = "Cake" };
        var expand = new ExpandCommand(row);
        row.JumpCommand = expand;
        var (window, _) = Host(row);
        var point = Headless.At(row, 0.5);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, expand.Calls);
        Assert.True(row.IsExpanded);
        Assert.False(row.IsEditing);
    }

    [AvaloniaFact]
    public void A_click_on_the_start_time_seeks_and_does_not_expand_or_rename()
    {
        var seeks = new RecordingCommand();
        var row = new ChapterListItem { Title = "Cake", Start = TimeSpan.FromSeconds(30), SeekCommand = seeks };
        var expand = new ExpandCommand(row);
        row.JumpCommand = expand;
        var (window, _) = Host(row);
        var start = row.GetVisualDescendants().OfType<Control>().Single(control => control.Name == ChapterListItem.StartPartName);
        var point = Headless.At(start, 0.5);

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(seeks.Calls);
        Assert.Equal(0, expand.Calls);
        Assert.False(row.IsEditing);
    }

    [AvaloniaFact]
    public void F2_does_not_open_the_inline_field_on_an_expanded_row()
    {
        var (window, row) = Host(new ChapterListItem { Title = "Cake", IsExpanded = true });
        row.Focus();

        window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);

        Assert.False(row.IsEditing);
    }

    private sealed class ExpandCommand(ChapterListItem row) : System.Windows.Input.ICommand
    {
        public int Calls { get; private set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { Calls++; row.IsExpanded = !row.IsExpanded; }
    }

    private static TextBox Editor(ChapterListItem row) => EditorOrNull(row) ?? throw new InvalidOperationException("No title field.");

    private static TextBox? EditorOrNull(ChapterListItem row) => row.GetVisualDescendants().OfType<TextBox>().SingleOrDefault();
}
