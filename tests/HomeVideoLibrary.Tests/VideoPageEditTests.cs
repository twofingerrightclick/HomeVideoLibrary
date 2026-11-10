using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using Xunit;
using static HomeVideoLibrary.Tests.VideoPages;

namespace HomeVideoLibrary.Tests;

/// <summary>The video page's wiring of its EDIT tab: the playhead, the I/O keys and the details' unsaved state.</summary>
public sealed class VideoPageEditTests
{
    [Fact]
    public async Task InAndOutMarkTheRangeAtThePlayheadAndShowTheEditTab()
    {
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();
        page.IsInspectorOpen = false;

        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 2_000));
        page.MarkInCommand.Execute(null);
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 6_000));
        page.MarkOutCommand.Execute(null);

        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)), (page.Edit.RangeStart, page.Edit.RangeEnd));
        Assert.Equal((int)InspectorTab.Edit, page.SelectedTab);
        Assert.True(page.IsInspectorOpen);
        Assert.Equal(TimeSpan.FromSeconds(6), page.Edit.SplitAt); // The split follows the playhead.
    }

    [Fact]
    public async Task UnsavedDetailsBlockTrimUntilTheyAreReverted()
    {
        await using var page = Create(new FakeMetadata(), new FakePlayback());
        await page.LoadAsync();
        page.Edit.OutText = "5";
        Assert.True(page.Edit.TrimCommand.CanExecute(null));
        var changes = 0;
        page.Edit.TrimCommand.CanExecuteChanged += (_, _) => changes++;

        page.Details.Title = "Renamed";
        Assert.False(page.Edit.TrimCommand.CanExecute(null));
        Assert.NotNull(page.Edit.UnsavedDetailsHint);

        page.Details.RevertCommand.Execute(null);
        Assert.True(page.Edit.TrimCommand.CanExecute(null));
        Assert.True(changes >= 2);
    }
}
