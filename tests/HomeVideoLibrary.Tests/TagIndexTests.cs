using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class TagIndexTests
{
    private static VideoRecord Tagged(IReadOnlyList<string> tags, DateTime modifiedUtc, params IReadOnlyList<string>[] chapterTags) =>
        Record("video", tags: tags, chapters: chapterTags.Select((chapter, index) => new VideoChapterRecord((ulong)index + 1,
            TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), "", "", chapter)).ToArray())
            with { Stamp = new FileStamp(1, modifiedUtc.Ticks) };

    [Fact]
    public void SuggestionsOrderByUsesThenRecencyThenName()
    {
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var recent = old.AddYears(3);
        var index = TagIndex.Build([
            Tagged(["beach", "birthday"], old),
            Tagged(["beach", "bike"], old, ["birthday"]),
            Tagged(["bonfire"], recent),
            Tagged(["barbecue"], old),
        ]);

        Assert.Equal(["beach", "birthday", "bonfire", "barbecue", "bike"], index.Suggest("b", 10).Select(tag => tag.Tag));
        var birthday = index.Find("Birthday")!;
        Assert.Equal((1, 1), (birthday.VideoCount, birthday.ChapterCount));
        Assert.Equal(["beach", "birthday"], index.Suggest("", 2).Select(tag => tag.Tag));
        Assert.Equal(["birthday", "bonfire"], index.Suggest("b", 2, exclude: ["BEACH"]).Select(tag => tag.Tag));
    }

    [Fact]
    public void MatchingFoldsCaseAndDiacriticsAndMatchesWordStarts()
    {
        var now = DateTime.UtcNow;
        var index = TagIndex.Build([Tagged(["Été"], now), Tagged(["été"], now), Tagged(["ete"], now), Tagged(["Birthday party"], now)]);

        var summer = Assert.Single(index.Suggest("ETE"));
        Assert.Equal(3, summer.VideoCount);
        Assert.Contains(summer.Tag, new[] { "Été", "été", "ete" });
        Assert.Equal("Birthday party", Assert.Single(index.Suggest("par")).Tag);
        Assert.Empty(index.Suggest("arty"));
    }

    [Fact]
    public void ApplyDiffsUpdatesAndRemovalsAndKeepsTheInstanceWhenTagsAreUnchanged()
    {
        var now = DateTime.UtcNow;
        var first = Tagged(["family", "beach"], now);
        var second = Tagged(["family"], now);
        var index = TagIndex.Build([first, second]);

        Assert.Same(index, index.Apply(first, first with { Description = "edited text" }));
        var retagged = index.Apply(first, first with { Tags = ["family", "picnic"] });
        Assert.Null(retagged.Find("beach"));
        Assert.Equal(1, retagged.Find("picnic")!.VideoCount);
        Assert.Equal(2, retagged.Find("family")!.VideoCount);

        var removed = retagged.Apply(second, null);
        Assert.Equal(1, removed.Find("family")!.VideoCount);
        Assert.Equal(2, removed.Count);
        Assert.Equal(0, TagIndex.Build([first]).Apply(first, null).Count);
    }
}
