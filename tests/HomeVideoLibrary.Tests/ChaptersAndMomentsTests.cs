using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using Xunit;
using static HomeVideoLibrary.Tests.VideoPages;

namespace HomeVideoLibrary.Tests;

public sealed class ChaptersAndMomentsTests
{
    [Fact]
    public async Task SelectedEntrySurvivesSeekAndEndActionConvertsWithoutTouchingMoments()
    {
        await using var page = Create(new FakeMetadata(), new FakePlayback());
        await page.LoadAsync();
        page.Player.Seek(TimeSpan.FromSeconds(2));
        page.AddMomentCommand.Execute(null);
        var row = Assert.Single(page.Details.Chapters.Chapters);
        Assert.True(row.IsMoment);
        page.Player.Seek(TimeSpan.FromSeconds(6));
        Assert.Same(row, page.Details.Chapters.ExpandedChapter);
        row.SetEndTimeCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(6), row.End);
        Assert.False(row.IsMoment);
        page.Player.Seek(TimeSpan.FromSeconds(4));
        page.AddMomentCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(6), row.End);
        Assert.True(page.Details.IsDirty);
        page.Player.Seek(TimeSpan.FromSeconds(2));
        row.SetEndTimeCommand.Execute(null);
        Assert.True(row.IsMoment);
    }

    [Fact]
    public async Task InvalidRangeMoveLeavesNeighborsAndEndIntact()
    {
        var metadata = new FakeMetadata { Stored = VideoMetadata.Empty with { Chapters = [
            new VideoChapter(1, TimeSpan.Zero, "A", "", []) { End = TimeSpan.FromSeconds(3) },
            new VideoChapter(2, TimeSpan.FromSeconds(4), "B", "", []) { End = TimeSpan.FromSeconds(8) }] } };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var row = page.Details.Chapters.Chapters[0];
        page.Player.Seek(TimeSpan.FromSeconds(5));
        row.MoveToPlayheadCommand.Execute(null);
        Assert.Equal(TimeSpan.Zero, row.Start);
        Assert.Equal(TimeSpan.FromSeconds(3), row.End);
        Assert.NotNull(row.TimingError);
        Assert.Equal(TimeSpan.FromSeconds(4), page.Details.Chapters.Chapters[1].Start);
    }
}
