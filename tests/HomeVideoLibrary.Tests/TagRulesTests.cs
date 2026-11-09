using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class TagRulesTests
{
    [Fact]
    public void SplitsOnEverySeparatorAndNormalizesWhitespace() =>
        Assert.Equal(["a b", "beach", "夏", "冬", "sunset"], TagRules.Split("  a \t b ,beach，夏、冬\r\nsunset\n,"));

    [Fact]
    public void IdentityIgnoresCaseDiacriticsAndWhitespaceRuns()
    {
        Assert.Equal(TagRules.Key("Été  Plage"), TagRules.Key("ete plage"));
        Assert.Equal(["Été", "Noël"], TagRules.Canonicalize(["Été, ete", "Noël", "NOEL"]));
        Assert.True(TagRules.Contains(["Café"], "cafe"));
    }

    [Fact]
    public void FileAndTypedSpellingsAreTheSameTag() =>
        Assert.Equal(MetadataText.NormalizeTags(["a  b"]), MetadataText.NormalizeTags(["a b"]));

    [Theory]
    [InlineData("Summer Beach", "bea", true)]
    [InlineData("Summer Beach", "SUM", true)]
    [InlineData("Été", "ete", true)]
    [InlineData("Summer Beach", "each", false)]
    [InlineData("anything", "", true)]
    public void PrefixMatchesTheStartOfAnyWord(string tag, string prefix, bool expected) =>
        Assert.Equal(expected, TagRules.MatchesPrefix(tag, prefix));
}
