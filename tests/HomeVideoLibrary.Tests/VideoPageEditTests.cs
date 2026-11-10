using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using Xunit;
using static HomeVideoLibrary.Tests.VideoPages;

namespace HomeVideoLibrary.Tests;

/// <summary>The video page around its EDIT tab: the I/O keys, which tab shows, what blocks an edit, and saving while
/// an edit job reads the file.</summary>
public sealed class VideoPageEditTests
{
    [Fact]
    public async Task InAndOutMarkTheRangeAndOpenACollapsedInspectorOnEdit()
    {
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();
        page.IsInspectorOpen = false;

        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 2_000, IsPlaying: false));
        page.MarkInCommand.Execute(null);
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 6_000));
        page.MarkOutCommand.Execute(null);

        var edit = page.Edit!;
        Assert.Equal((TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)), (edit.RangeStart, edit.RangeEnd));
        Assert.Equal((int)InspectorTab.Edit, page.SelectedTab);
        Assert.True(page.IsInspectorOpen);
        Assert.Equal(TimeSpan.FromSeconds(6), edit.SplitAt); // Paused with EDIT shown: the split point follows seeks.
    }

    [Fact]
    public async Task TheSplitPointIgnoresPlaybackTicksAndOtherTabs()
    {
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();
        page.ShowTab(InspectorTab.Edit);
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 1_000, IsPlaying: false));
        Assert.Equal(TimeSpan.FromSeconds(1), page.Edit!.SplitAt);

        playback.Raise(new PlaybackUpdate(playback.Version, IsPlaying: true));
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 3_000));
        Assert.Equal(TimeSpan.FromSeconds(1), page.Edit.SplitAt);
        page.ShowTab(InspectorTab.Info);
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 4_000, IsPlaying: false));
        Assert.Equal(TimeSpan.FromSeconds(1), page.Edit.SplitAt);

        page.ShowTab(InspectorTab.Edit);
        Assert.Equal(TimeSpan.FromSeconds(4), page.Edit.SplitAt);
    }

    [Fact]
    public async Task OpeningOnTheEditTabOpensACollapsedInspector()
    {
        var session = new VideoPageSession { IsInspectorOpen = false, SelectedTab = (int)InspectorTab.Info };
        await using var page = Create(new FakeMetadata(), new FakePlayback(), session: session,
            configure: dependencies => dependencies with { StartTab = InspectorTab.Edit });

        Assert.Equal((int)InspectorTab.Edit, page.SelectedTab);
        Assert.True(page.IsInspectorOpen);
    }

    [Fact]
    public async Task UnsavedDetailsBlockEditsUntilTheyAreReverted()
    {
        await using var page = Create(new FakeMetadata(), new FakePlayback());
        await page.LoadAsync();
        var edit = page.Edit!;
        var changes = 0;
        edit.TrimCommand.CanExecuteChanged += (_, _) => changes++;

        page.Details.Title = "Renamed";
        Assert.Equal("Save or revert your changes first", edit.EditBlocker);

        page.Details.RevertCommand.Execute(null);
        Assert.Null(edit.EditBlocker);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task WithoutEditingToolsThereIsNoEditTab()
    {
        await using var page = Create(new FakeMetadata(), new FakePlayback(), configure: dependencies => dependencies with { Edits = null });

        Assert.Null(page.Edit);
        Assert.False(page.MarkInCommand.CanExecute(null));
    }

    [Fact]
    public async Task SaveWaitsWhileAnEditJobReadsTheVideo()
    {
        var jobs = new JobQueue(_ => Task.CompletedTask);
        await using var page = Create(new FakeMetadata(), new FakePlayback(), configure: dependencies => dependencies with { Jobs = jobs });
        await page.LoadAsync();
        page.Details.Title = "Renamed";
        Assert.True(page.Details.SaveCommand.CanExecute(null));
        var changes = 0;
        page.Details.SaveCommand.CanExecuteChanged += (_, _) => changes++;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var job = jobs.Enqueue("Trim", JobLane.Render, async (_, _) => { await release.Task; return JobOutcome.Done("Saved."); }, [page.Id]);
        Assert.False(page.Details.SaveCommand.CanExecute(null));
        Assert.Equal(VideoPageViewModel.HeldBySaveMessage, page.SaveBlocker);

        release.SetResult();
        await job.Completion;
        Assert.True(page.Details.SaveCommand.CanExecute(null));
        Assert.Null(page.SaveBlocker);
        Assert.True(changes >= 2);
    }
}
