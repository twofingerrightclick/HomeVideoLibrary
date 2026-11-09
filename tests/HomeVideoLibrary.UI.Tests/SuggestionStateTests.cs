using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public sealed class SuggestionStateTests
{
    private static readonly string[] Offered = ["Grandma", "Grandpa", "graduation"];

    [Fact]
    public void Closed_offers_nothing_for_an_empty_entry_but_lists_matches_for_text()
    {
        Assert.False(SuggestionState.Closed.WantsList(null));
        Assert.True(SuggestionState.Closed.WantsList("gr"));
    }

    [Fact]
    public void Browsing_lists_everything_until_text_is_typed()
    {
        var browsing = SuggestionState.Closed.Browse();
        Assert.True(browsing.WantsList(null));

        Assert.False(browsing.TextChanged("z").IsBrowsing);
        Assert.True(browsing.TextChanged(null).IsBrowsing);
    }

    [Fact]
    public void Dismissing_hides_the_list_until_the_text_changes()
    {
        var dismissed = SuggestionState.Closed.Move(Offered, 1).Dismiss();

        Assert.False(dismissed.WantsList("gr"));
        Assert.Null(dismissed.Highlighted);
        Assert.True(dismissed.TextChanged("gra").WantsList("gra"));
    }

    [Fact]
    public void Moving_from_no_highlight_picks_the_first_or_last_and_then_wraps()
    {
        Assert.Equal("Grandma", SuggestionState.Closed.Move(Offered, 1).Highlighted);
        Assert.Equal("graduation", SuggestionState.Closed.Move(Offered, -1).Highlighted);
        Assert.Equal("Grandma", SuggestionState.Closed.Move(Offered, -1).Move(Offered, 1).Highlighted);
    }

    [Fact]
    public void The_highlight_is_kept_by_tag_identity_and_dropped_when_no_longer_offered()
    {
        var state = SuggestionState.Closed.Move(Offered, 1).Move(Offered, 1);

        Assert.Equal("Grandpa", state.Reconcile(["GRANDPA", "Grandma"]).Highlighted);
        Assert.Null(state.Reconcile(["Grandma"]).Highlighted);
    }

    [Fact]
    public void Typing_drops_the_highlight()
    {
        Assert.Null(SuggestionState.Closed.Move(Offered, 1).TextChanged("gran").Highlighted);
    }
}
