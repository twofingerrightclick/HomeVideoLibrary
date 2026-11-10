using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public sealed class TagSuggestionsTests
{
    [Theory]
    [InlineData("grandma", "gr", 0, TagMatchKind.Prefix)]
    [InlineData("uncle greg", "gr", 6, TagMatchKind.WordStart)]
    [InlineData("fair-ground", "gr", 5, TagMatchKind.WordStart)]
    [InlineData("big_grill", "gr", 4, TagMatchKind.WordStart)]
    [InlineData("fairground", "gr", 4, TagMatchKind.Substring)]
    [InlineData("beach", "gr", -1, TagMatchKind.None)]
    public void FindMatch_ReportsBestTier(string tag, string query, int start, TagMatchKind kind)
    {
        var match = TagSuggestions.FindMatch(tag, query);
        Assert.Equal(start, match.Start);
        Assert.Equal(kind, match.Kind);
    }

    [Fact]
    public void FindMatch_PrefersLaterWordStartOverEarlierSubstring() =>
        Assert.Equal(new TagMatch(9, 2, TagMatchKind.WordStart), TagSuggestions.FindMatch("grandmas mall", "ma"));

    [Fact]
    public void Rank_EmptyQueryOrdersByCountThenAlphabetically()
    {
        TagFrequency[] source = [new("beach", 3), new("Christmas", 9), new("attic", 3), new("dog", 1)];

        var ranked = TagSuggestions.Rank("  ", source, [], 10);

        Assert.Equal(["Christmas", "attic", "beach", "dog"], ranked.Select(r => r.Tag));
    }

    [Fact]
    public void Rank_OrdersPrefixThenWordStartThenSubstring()
    {
        TagFrequency[] source =
        [
            new("fairground", 50),
            new("uncle greg", 20),
            new("graduation", 2),
            new("grandma", 9),
            new("beach", 99),
        ];

        var ranked = TagSuggestions.Rank("GR", source, [], 10);

        Assert.Equal(["grandma", "graduation", "uncle greg", "fairground"], ranked.Select(r => r.Tag));
    }

    [Fact]
    public void Rank_ExcludesChosenTagsIgnoringCase()
    {
        TagFrequency[] source = [new("Grandma", 9), new("grandpa", 4)];

        var ranked = TagSuggestions.Rank("gr", source, ["GRANDMA"], 10);

        Assert.Equal(["grandpa"], ranked.Select(r => r.Tag));
    }

    [Fact]
    public void Rank_MergesCaseVariantsKeepingMostUsedCasing()
    {
        TagFrequency[] source = [new("beach", 2), new("Beach", 5), new("BEACH", 1), new("pool", 7)];

        var ranked = TagSuggestions.Rank(null, source, [], 10);

        Assert.Equal([new TagFrequency("Beach", 8), new TagFrequency("pool", 7)], ranked);
    }

    [Fact]
    public void Rank_CapsAtMax()
    {
        var source = Enumerable.Range(0, 20).Select(i => new TagFrequency($"tag {i:00}", i));

        var ranked = TagSuggestions.Rank("tag", source, [], 3);

        Assert.Equal(["tag 19", "tag 18", "tag 17"], ranked.Select(r => r.Tag));
        Assert.Empty(TagSuggestions.Rank("tag", source, [], 0));
    }

    [Fact]
    public void Rank_NormalizesQueryWhitespace()
    {
        TagFrequency[] source = [new("summer 1994", 3), new("summer camp", 1)];

        var ranked = TagSuggestions.Rank("  summer   19 ", source, [], 10);

        Assert.Equal(["summer 1994"], ranked.Select(r => r.Tag));
    }
}
