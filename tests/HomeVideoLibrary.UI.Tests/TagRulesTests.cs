using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public sealed class TagRulesTests
{
    [Theory]
    [InlineData("  beach  ", "beach")]
    [InlineData("summer \t  1994", "summer 1994")]
    [InlineData("a\nb", "a b")]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_TrimsAndCollapsesWhitespace(string? raw, string? expected) =>
        Assert.Equal(expected, TagRules.Normalize(raw));

    [Fact]
    public void SplitEntry_SplitsOnCommasAndNormalizesEachPiece() =>
        Assert.Equal(["beach", "Grandma", "summer 1994"], TagRules.SplitEntry("beach, Grandma ,  summer 1994"));

    [Fact]
    public void SplitEntry_DropsEmptyPiecesAndCaseInsensitiveRepeats() =>
        Assert.Equal(["Beach", "pool"], TagRules.SplitEntry(",Beach,, beach ,pool,"));

    [Fact]
    public void Contains_IgnoresCase()
    {
        Assert.True(TagRules.Contains(["Christmas"], "christmas"));
        Assert.False(TagRules.Contains(["Christmas"], "Christmas Eve"));
    }

    [Fact]
    public void Distinct_KeepsFirstCasing() =>
        Assert.Equal(["Lake", "dock"], TagRules.Distinct(["Lake", "lake", "dock", "LAKE"]));

    [Theory]
    [InlineData("grandma", "gr", 0, TagMatchKind.Prefix)]
    [InlineData("uncle greg", "gr", 6, TagMatchKind.WordStart)]
    [InlineData("fair-ground", "gr", 5, TagMatchKind.WordStart)]
    [InlineData("big_grill", "gr", 4, TagMatchKind.WordStart)]
    [InlineData("fairground", "gr", 4, TagMatchKind.Substring)]
    [InlineData("beach", "gr", -1, TagMatchKind.None)]
    public void FindMatch_ReportsBestTier(string tag, string query, int start, TagMatchKind kind)
    {
        var match = TagRules.FindMatch(tag, query);
        Assert.Equal(start, match.Start);
        Assert.Equal(kind, match.Kind);
    }

    [Fact]
    public void FindMatch_PrefersLaterWordStartOverEarlierSubstring() =>
        Assert.Equal(new TagMatch(9, 2, TagMatchKind.WordStart), TagRules.FindMatch("grandmas mall", "ma"));

    [Fact]
    public void RankSuggestions_EmptyQueryOrdersByCountThenAlphabetically()
    {
        TagFrequency[] source = [new("beach", 3), new("Christmas", 9), new("attic", 3), new("dog", 1)];

        var ranked = TagRules.RankSuggestions("  ", source, [], 10);

        Assert.Equal(["Christmas", "attic", "beach", "dog"], ranked.Select(r => r.Tag));
    }

    [Fact]
    public void RankSuggestions_OrdersPrefixThenWordStartThenSubstring()
    {
        TagFrequency[] source =
        [
            new("fairground", 50),
            new("uncle greg", 20),
            new("graduation", 2),
            new("grandma", 9),
            new("beach", 99),
        ];

        var ranked = TagRules.RankSuggestions("GR", source, [], 10);

        Assert.Equal(["grandma", "graduation", "uncle greg", "fairground"], ranked.Select(r => r.Tag));
    }

    [Fact]
    public void RankSuggestions_ExcludesChosenTagsIgnoringCase()
    {
        TagFrequency[] source = [new("Grandma", 9), new("grandpa", 4)];

        var ranked = TagRules.RankSuggestions("gr", source, ["GRANDMA"], 10);

        Assert.Equal(["grandpa"], ranked.Select(r => r.Tag));
    }

    [Fact]
    public void RankSuggestions_MergesCaseVariantsKeepingMostUsedCasing()
    {
        TagFrequency[] source = [new("beach", 2), new("Beach", 5), new("BEACH", 1), new("pool", 7)];

        var ranked = TagRules.RankSuggestions(null, source, [], 10);

        Assert.Equal([new TagFrequency("Beach", 8), new TagFrequency("pool", 7)], ranked);
    }

    [Fact]
    public void RankSuggestions_CapsAtMax()
    {
        var source = Enumerable.Range(0, 20).Select(i => new TagFrequency($"tag {i:00}", i));

        var ranked = TagRules.RankSuggestions("tag", source, [], 3);

        Assert.Equal(["tag 19", "tag 18", "tag 17"], ranked.Select(r => r.Tag));
        Assert.Empty(TagRules.RankSuggestions("tag", source, [], 0));
    }

    [Fact]
    public void RankSuggestions_NormalizesQueryWhitespace()
    {
        TagFrequency[] source = [new("summer 1994", 3), new("summer camp", 1)];

        var ranked = TagRules.RankSuggestions("  summer   19 ", source, [], 10);

        Assert.Equal(["summer 1994"], ranked.Select(r => r.Tag));
    }
}
