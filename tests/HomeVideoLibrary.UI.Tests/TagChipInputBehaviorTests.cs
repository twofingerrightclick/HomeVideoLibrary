using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;
using Xunit;
using static HomeVideoLibrary.UI.Tests.Headless;

namespace HomeVideoLibrary.UI.Tests;

public sealed class TagChipInputBehaviorTests
{
    private static readonly TagSuggestionProvider Library = TagSuggestions.FromSnapshot(
        [new("Grandma", 40), new("graduation", 9), new("Uncle Greg", 12), new("beach", 3)]);

    [AvaloniaFact]
    public void Enter_asks_to_add_the_tag_in_the_librarys_spelling_without_touching_Tags()
    {
        var add = new RecordingCommand();
        string[] tags = ["beach"];
        var (window, input) = Host(new TagChipInput { Tags = tags, AddTagCommand = add, SuggestionProvider = Library });
        Entry(input).Focus();

        window.KeyTextInput("grandma");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Equal(["Grandma"], add.Calls);
        Assert.Same(tags, input.Tags);
        Assert.True(string.IsNullOrEmpty(input.Text));
    }

    [AvaloniaFact]
    public void A_tag_already_present_is_not_requested_again()
    {
        var add = new RecordingCommand();
        var (window, input) = Host(new TagChipInput { Tags = ["Beach"], AddTagCommand = add });
        Entry(input).Focus();

        window.KeyTextInput("BEACH,");

        Assert.Empty(add.Calls);
    }

    [AvaloniaFact]
    public void A_chips_X_asks_to_remove_that_tag()
    {
        var remove = new RecordingCommand();
        var (window, input) = Host(new TagChipInput { Tags = ["one", "two"], RemoveTagCommand = remove });
        var x = input.GetVisualDescendants().OfType<TagChip>().Last().GetVisualDescendants().OfType<Button>().Single();

        window.MouseDown(At(x, 0.5), MouseButton.Left);
        window.MouseUp(At(x, 0.5), MouseButton.Left);

        Assert.Equal(["two"], remove.Calls);
    }

    [AvaloniaFact]
    public void Leaving_the_field_commits_the_draft()
    {
        var add = new RecordingCommand();
        var other = new Button();
        var input = new TagChipInput { AddTagCommand = add };
        var (window, _) = Host(new StackPanel { Children = { input, other } });
        Entry(input).Focus();
        window.KeyTextInput("pending");

        other.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["pending"], add.Calls);
        Assert.True(string.IsNullOrEmpty(input.Text));
    }

    [AvaloniaFact]
    public void Backspace_asks_to_remove_a_chip_only_on_a_fresh_unmodified_press()
    {
        var remove = new RecordingCommand();
        var (window, input) = Host(new TagChipInput { Tags = ["one", "two"], RemoveTagCommand = remove });
        Entry(input).Focus();

        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.Control);
        window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.Control);
        Assert.Empty(remove.Calls);

        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
        Assert.Equal(["two"], remove.Calls);
    }

    [AvaloniaFact]
    public void Suggestions_come_from_the_provider_with_the_query_and_current_tags()
    {
        (string? Query, string[] Exclude, int Max)? asked = null;
        TagSuggestionProvider provider = (query, exclude, max) =>
        {
            asked = (query, [.. exclude], max);
            return [new("zebra", 1), new("Grandma", 40)];
        };
        var (window, input) = Host(new TagChipInput { Tags = ["beach"], SuggestionProvider = provider, MaxSuggestions = 5 });
        Entry(input).Focus();

        window.KeyTextInput("gr");

        Assert.Equal("gr", asked!.Value.Query);
        Assert.Equal(["beach"], asked.Value.Exclude);
        Assert.Equal(5, asked.Value.Max);
        Assert.Equal(["zebra", "Grandma"], input.Suggestions.Select(r => r.Tag));
        Assert.Equal(0, input.Suggestions[1].MatchStart);
    }

    [AvaloniaFact]
    public void The_highlight_follows_its_tag_when_suggestions_reorder()
    {
        var (window, input) = Host(new TagChipInput { SuggestionProvider = Library });
        Entry(input).Focus();
        window.KeyTextInput("g");
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        var highlighted = input.HighlightedSuggestion!.Tag;

        input.SuggestionProvider = (query, exclude, max) => [.. Library(query, exclude, max).Reverse()];

        Assert.Equal(highlighted, input.HighlightedSuggestion!.Tag);
    }

    private static TextBox Entry(TagChipInput input) => input.GetVisualDescendants().OfType<TextBox>().Single();
}
