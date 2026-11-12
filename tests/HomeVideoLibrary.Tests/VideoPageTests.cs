using System.Collections.Specialized;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.Playback;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static HomeVideoLibrary.Tests.VideoPages;

namespace HomeVideoLibrary.Tests;

public sealed class VideoPageTests
{
    [Fact]
    public async Task LoadingIsCleanAndChapterEditsBecomeDirty()
    {
        await using var page = Create(new FakeMetadata(), new FakePlayback());
        await page.LoadAsync();
        Assert.False(page.Details.IsDirty);

        page.Player.Seek(TimeSpan.FromSeconds(4));
        page.Details.Chapters.AddChapterCommand.Execute(null);
        var chapter = Assert.Single(page.Details.Chapters.Chapters);
        Assert.NotEqual(0UL, chapter.Uid);
        Assert.Equal(TimeSpan.FromSeconds(4), chapter.Start);
        Assert.True(page.Details.IsDirty);

        chapter.Description = "A memory";
        await page.Details.SaveCommand.ExecuteAsync();
        Assert.False(page.Details.IsDirty);
        Assert.Same(chapter, Assert.Single(page.Details.Chapters.Chapters));
        Assert.Equal("A memory", chapter.Description);

        chapter.Tags.AddTagCommand.Execute("family");
        chapter.Tags.AddTagCommand.Execute("Family");
        Assert.True(page.Details.IsDirty);
        Assert.Equal(["family"], chapter.ToChapter().Tags);
    }

    [Fact]
    public async Task SaveCanReleasePlaybackThenRestoresPositionAndState()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => Assert.True(playback.Closed) };
        await using var page = Create(metadata, playback, releasePlayback: true);
        await page.LoadAsync();
        page.Player.Seek(TimeSpan.FromSeconds(3.5));
        page.Details.Description = "Saved memory";

        await page.Details.SaveCommand.ExecuteAsync();

        Assert.Equal("Saved memory", metadata.Saved!.Description);
        Assert.Equal(TimeSpan.FromSeconds(3.5), playback.LastStart);
        Assert.True(playback.LastPlay);
        Assert.False(page.Details.IsDirty);
        Assert.Same(playback.Created[^1], page.Player.Surface);
        Assert.Equal(2, playback.Opens);
    }

    [Fact]
    public async Task SeeksDuringASaveThatReleasesPlaybackAreKeptAndTheLastOneWins()
    {
        var saving = new TaskCompletionSource();
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { SaveOperation = _ => saving.Task };
        await using var page = Create(metadata, playback, releasePlayback: true);
        await page.LoadAsync();
        page.Details.Title = "Edited";

        var save = page.Details.SaveCommand.ExecuteAsync();
        Assert.True(playback.Closed);
        Assert.True(page.Player.SeekCommand.CanExecute(TimeSpan.FromSeconds(2)));
        page.Player.SeekCommand.Execute(TimeSpan.FromSeconds(2));
        page.Player.SeekCommand.Execute(TimeSpan.FromSeconds(7));
        saving.SetResult();
        await save;

        Assert.Equal(TimeSpan.FromSeconds(7), playback.LastStart);
        Assert.Equal(TimeSpan.FromSeconds(7), page.Player.Position);
    }

    [Fact]
    public async Task SeeksWhileOneIsInFlightCoalesceToTheLatest()
    {
        var gate = new TaskCompletionSource();
        var playback = new FakePlayback { SeekGate = gate.Task };
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();

        page.Player.Seek(TimeSpan.FromSeconds(1));
        page.Player.Seek(TimeSpan.FromSeconds(2));
        page.Player.Seek(TimeSpan.FromSeconds(3));
        Assert.Equal([TimeSpan.FromSeconds(1)], playback.Seeks);
        playback.SeekGate = null;
        gate.SetResult();
        await Task.Yield();

        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)], playback.Seeks);
        Assert.Equal(TimeSpan.FromSeconds(3), page.Player.Position);
    }

    [Fact]
    public async Task InPlaceSaveKeepsPlaybackOpenAndShowsTheSavedDetails()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => Assert.False(playback.Closed) };
        await using var page = Create(metadata, playback);
        await page.LoadAsync();
        var opens = playback.Opens;
        page.Details.Tags.AddTagCommand.Execute("beach");
        page.Details.Tags.AddTagCommand.Execute("Beach");
        page.Details.Tags.AddTagCommand.Execute(" sunset ");

        await page.Details.SaveCommand.ExecuteAsync();

        Assert.Equal(["beach", "sunset"], metadata.Saved!.Tags);
        Assert.Equal(opens, playback.Opens);
        Assert.Equal(["beach", "sunset"], page.Details.Tags.Items);
        Assert.False(page.Details.IsDirty);
    }

    [Fact]
    public async Task PlaybackStartsEvenWhenTheMetadataReadFails()
    {
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata { ReadError = new InvalidDataException("bad manifest") }, playback);

        await page.LoadAsync();

        Assert.Equal(1, playback.Opens);
        Assert.False(page.Details.CanEdit);
        Assert.False(page.Details.Chapters.AddChapterCommand.CanExecute(null));
        Assert.Contains("bad manifest", page.Status);
    }

    [Fact]
    public async Task LoadingFinishesAndEditingStartsWhilePlaybackIsStillOpening()
    {
        var opening = new TaskCompletionSource();
        var playback = new FakePlayback { OpenGate = opening.Task };
        var page = Create(new FakeMetadata(), playback);

        await page.LoadAsync();

        Assert.True(page.Details.CanEdit);
        Assert.False(page.Player.IsPlaying);
        opening.SetResult();
        Assert.True(page.Player.IsPlaying);
        await page.DisposeAsync();
        Assert.Contains("dispose", playback.Calls);
    }

    [Fact]
    public async Task TheLibraryRecordFillsTheDetailsBeforeTheFileIsRead()
    {
        var record = Record(VideoMetadata.Empty with { Title = "Beach", Tags = ["sun"] });
        await using var page = Create(new FakeMetadata(), new FakePlayback(), record);

        Assert.Equal("Beach", page.Details.Title);
        Assert.Equal(["sun"], page.Details.Tags.Items);
        Assert.False(page.Details.IsDirty);
        Assert.False(page.Details.CanEdit);
    }

    [Fact]
    public async Task FailedPlaybackStartReleasesThePlayer()
    {
        var playback = new FakePlayback { StartError = new InvalidOperationException("The video could not be played.") };
        await using var page = Create(new FakeMetadata(), playback);

        await page.LoadAsync();

        Assert.Equal(["create", "start", "close"], playback.Calls);
        Assert.Null(page.Player.Surface);
        Assert.Equal(PlaybackState.Failed, page.Player.State);
        Assert.False(page.Player.IsPlaying);
        Assert.True(page.Details.CanEdit);
        Assert.Contains("could not be played", page.Status);
    }

    [Fact]
    public async Task DirtyIsTheDifferenceFromTheLoadedState()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Title = "Beach", Tags = ["sun"], Chapters = [new VideoChapter(9, TimeSpan.FromTicks(12_345_678), "Swim", "", [])] },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        Assert.False(page.Details.IsDirty);

        page.Details.Title = "Lake";
        Assert.True(page.Details.IsDirty);
        page.Details.Title = "Beach";
        Assert.False(page.Details.IsDirty);

        page.Details.Tags.AddTagCommand.Execute("Sun");
        Assert.False(page.Details.IsDirty);
        var chapter = page.Details.Chapters.Chapters[0];
        chapter.CommitTitleCommand.Execute("Dive");
        Assert.True(page.Details.IsDirty);
        chapter.CommitTitleCommand.Execute("Swim");
        Assert.False(page.Details.IsDirty);
        Assert.Equal(TimeSpan.FromTicks(12_345_678), chapter.ToChapter().Position);
    }

    [Fact]
    public async Task SaveKeepsUnchangedRowsAndAdoptsTheStoredForm()
    {
        var thumbnail = new VideoThumbnail(Guid.NewGuid(), TimeSpan.FromSeconds(1), "/unused/thumb.jpg");
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(9, TimeSpan.FromSeconds(2), "Swim", "", [])], Thumbnails = [thumbnail] },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var chapterRow = page.Details.Chapters.Chapters[0];
        var thumbnailRow = page.Details.Frames.Thumbnails[0];
        var reads = metadata.Reads;
        page.Details.Title = "  Beach  ";

        await page.Details.SaveCommand.ExecuteAsync();

        Assert.Same(chapterRow, Assert.Single(page.Details.Chapters.Chapters));
        Assert.Same(thumbnailRow, Assert.Single(page.Details.Frames.Thumbnails));
        Assert.Equal(reads, metadata.Reads);
        Assert.Equal("Beach", page.Details.Title);
        Assert.False(page.Details.IsDirty);
    }

    [Fact]
    public async Task EditsTypedWhileASaveRunsStayAsUnsavedChanges()
    {
        var saving = new TaskCompletionSource();
        var metadata = new FakeMetadata { SaveOperation = _ => saving.Task };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        page.Details.Title = "First";

        var save = page.Details.SaveCommand.ExecuteAsync();
        page.Details.Description = "Typed during the save";
        saving.SetResult();
        await save;

        Assert.Equal("First", metadata.Saved!.Title);
        Assert.Equal("First", page.Details.Title);
        Assert.Equal("Typed during the save", page.Details.Description);
        Assert.True(page.Details.IsDirty);
    }

    [Fact]
    public async Task MissingTitleStaysBlankWithTheFileNameAsWatermark()
    {
        await using var page = Create(new FakeMetadata(), new FakePlayback());
        await page.LoadAsync();
        Assert.Equal("", page.Details.Title);
        Assert.Equal("Test video", page.Details.TitleWatermark);
        Assert.Equal("Test video", page.DisplayTitle);
        Assert.True(page.HasBackKey); // The shell does not add a second back key.
    }

    [Fact]
    public async Task PartialRecordedDateIsShownAndKeptUnlessEdited()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { RecordedAt = new DateTimeOffset(1997, 1, 1, 0, 0, 0, TimeSpan.Zero), RecordedAtText = "1997" },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        Assert.Equal("1997", page.Details.RecordedAtText);

        page.Details.Description = "Unrelated";
        await page.Details.SaveCommand.ExecuteAsync();
        Assert.Equal("1997", metadata.Saved!.RecordedAtText);

        page.Details.RecordedAtText = "1997-07";
        await page.Details.SaveCommand.ExecuteAsync();
        Assert.Equal(new DateTimeOffset(1997, 7, 1, 0, 0, 0, TimeSpan.Zero), metadata.Saved!.RecordedAt);
        Assert.Equal("1997-07", RecordedDate.ToTagText(metadata.Saved));
    }

    [Fact]
    public async Task AnInvalidRecordedDateIsExplainedAndBlocksSaving()
    {
        var metadata = new FakeMetadata();
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();

        page.Details.RecordedAtText = "July 97";

        Assert.NotNull(page.Details.RecordedDateError);
        Assert.False(page.Details.IsValid);
        Assert.False(page.Details.SaveCommand.CanExecute(null));
        page.Details.RecordedAtText = "1997-07";
        Assert.Null(page.Details.RecordedDateError);
        Assert.True(page.Details.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedSaveRetainsDirtyEdits()
    {
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { OnSave = () => throw new IOException("disk is full") };
        await using var page = Create(metadata, playback);
        await page.LoadAsync();
        page.Details.Description = "Keep these edits";

        await page.Details.SaveCommand.ExecuteAsync();

        Assert.True(page.Details.IsDirty);
        Assert.Equal("Keep these edits", page.Details.Description);
        Assert.Contains("disk is full", page.Status);
        Assert.False(playback.Closed);
    }

    [Fact]
    public async Task CancellingSaveRetainsDirtyEditsAndPlayback()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new FakePlayback();
        var metadata = new FakeMetadata { SaveOperation = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); } };
        await using var page = Create(metadata, playback);
        await page.LoadAsync();
        page.Details.Description = "Keep cancelled edits";

        var saving = page.Details.SaveCommand.ExecuteAsync();
        await started.Task;
        page.Details.SaveCommand.Cancel();
        await saving;

        Assert.True(page.Details.IsDirty);
        Assert.False(page.Details.SaveCommand.IsRunning);
        Assert.False(playback.Closed);
    }

    [Fact]
    public async Task RevertRestoresTheBaselineAndClearsDirty()
    {
        var metadata = new FakeMetadata { Stored = VideoMetadata.Empty with { Title = "Beach", Chapters = [new VideoChapter(9, TimeSpan.FromSeconds(2), "Swim", "", [])] } };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var row = page.Details.Chapters.Chapters[0];
        page.Details.Title = "Lake";
        row.CommitTitleCommand.Execute("Dive");
        page.Player.Seek(TimeSpan.FromSeconds(6));
        page.Details.Chapters.AddChapterCommand.Execute(null);
        Assert.True(page.Details.RevertCommand.CanExecute(null));

        page.Details.RevertCommand.Execute(null);

        Assert.False(page.Details.IsDirty);
        Assert.Equal("Beach", page.Details.Title);
        Assert.Same(row, Assert.Single(page.Details.Chapters.Chapters));
        Assert.Equal("Swim", row.Title);
        Assert.False(page.Details.RevertCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpeningAnotherVideoStartsWithAnEmptyTagDraftAndNoEditRow()
    {
        await using var first = Create(new FakeMetadata(), new FakePlayback());
        await first.LoadAsync();
        first.Details.Tags.Draft = "half-typed";
        first.Player.Seek(TimeSpan.FromSeconds(3));
        first.Details.Chapters.AddChapterCommand.Execute(null);
        Assert.NotNull(first.Details.Chapters.ExpandedChapter);

        await using var second = Create(new FakeMetadata(), new FakePlayback());
        await second.LoadAsync();

        Assert.Null(second.Details.Tags.Draft);
        Assert.Null(second.Details.Chapters.ExpandedChapter);
    }

    [Fact]
    public async Task PlayheadTicksDuringATitleEditKeepTheRowAndItsDraft()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(1, TimeSpan.Zero, "Start", "", []), new VideoChapter(2, TimeSpan.FromSeconds(5), "Middle", "", []) { End = TimeSpan.FromSeconds(10) }] },
        };
        var playback = new FakePlayback();
        await using var page = Create(metadata, playback);
        await page.LoadAsync();
        var row = page.Details.Chapters.Chapters[1];
        page.Details.Chapters.ExpandedChapter = row;
        row.DraftTitle = "Half a tit";
        var changes = new List<NotifyCollectionChangedEventArgs>();
        page.Details.Chapters.Rows.CollectionChanged += (_, args) => changes.Add(args);

        for (var tick = 0; tick < 20; tick++)
            playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: tick * 450));

        Assert.Same(row, page.Details.Chapters.Chapters[1]);
        Assert.True(row.IsExpanded);
        Assert.Equal("Half a tit", row.DraftTitle);
        Assert.NotEmpty(changes);
        Assert.All(changes, change =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Move, change.Action);
            Assert.Same(page.Details.Chapters.Placeholder, Assert.Single(change.OldItems!));
        });
        Assert.True(row.IsCurrent);
    }

    [Fact]
    public async Task ThePlaceholderMovesWithThePlayheadAndHidesAtAChapter()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(1, TimeSpan.FromSeconds(2), "A", "", []), new VideoChapter(2, TimeSpan.FromSeconds(6), "B", "", [])] },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var chapters = page.Details.Chapters;

        page.Player.Seek(TimeSpan.FromSeconds(4));
        Assert.Equal(1, chapters.Rows.IndexOf(chapters.Placeholder));
        Assert.True(chapters.Placeholder.IsShown);
        page.Player.Seek(TimeSpan.FromSeconds(8));
        Assert.Equal(2, chapters.Rows.IndexOf(chapters.Placeholder));
        page.Player.Seek(TimeSpan.FromSeconds(6));
        chapters.AddChapterCommand.Execute(null);
        Assert.False(chapters.Placeholder.IsShown);
        Assert.False(chapters.AddChapterCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddingAtThePlayheadInsertsInOrderAndASecondAddAtTheSameTimeDoesNothing()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(1, TimeSpan.FromSeconds(1), "A", "", []), new VideoChapter(2, TimeSpan.FromSeconds(8), "B", "", [])] },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var chapters = page.Details.Chapters;
        page.Player.Seek(TimeSpan.FromSeconds(4));

        chapters.AddChapterCommand.Execute(null);
        page.Player.Seek(TimeSpan.FromSeconds(4));
        chapters.AddChapterCommand.Execute(null);

        Assert.Equal(["A", "", "B"], chapters.Chapters.Select(row => row.Title));
        var added = chapters.Chapters[1];
        Assert.True(added.IsExpanded);
        Assert.Same(added, chapters.ExpandedChapter);
        Assert.Equal(TimeSpan.FromSeconds(4), added.Start);
        Assert.Equal(added, chapters.Rows.OfType<ChapterRowViewModel>().ElementAt(1));
        Assert.Equal(["A", "", "B"], chapters.Marks.Select(mark => mark.Title));
    }

    [Fact]
    public async Task MovingAChapterToThePlayheadReordersRowsByMoveOnly()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with
            {
                Chapters = [new VideoChapter(1, TimeSpan.FromSeconds(1), "A", "", []), new VideoChapter(2, TimeSpan.FromSeconds(3), "B", "", []),
                    new VideoChapter(3, TimeSpan.FromSeconds(5), "C", "", [])],
            },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var chapters = page.Details.Chapters;
        var first = chapters.Chapters[0];
        page.Player.Seek(TimeSpan.FromSeconds(8));
        var changes = new List<NotifyCollectionChangedAction>();
        chapters.Rows.CollectionChanged += (_, args) => changes.Add(args.Action);

        first.MoveToPlayheadCommand.Execute(null);

        Assert.Equal(["B", "C", "A"], chapters.Chapters.Select(row => row.Title));
        Assert.Equal(["B", "C", "A"], chapters.Rows.OfType<ChapterRowViewModel>().Select(row => row.Title));
        Assert.NotEmpty(changes);
        Assert.All(changes, action => Assert.Equal(NotifyCollectionChangedAction.Move, action));
        Assert.True(page.Details.IsDirty);
    }

    [Fact]
    public async Task ChapterKeysJumpBetweenChapterStarts()
    {
        var metadata = new FakeMetadata
        {
            Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(1, TimeSpan.FromSeconds(2), "A", "", []), new VideoChapter(2, TimeSpan.FromSeconds(6), "B", "", [])] },
        };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var chapters = page.Details.Chapters;

        page.Player.Seek(TimeSpan.FromSeconds(4));
        chapters.NextChapterCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(6), page.Player.Position);
        page.Player.Seek(TimeSpan.FromSeconds(7));
        chapters.PreviousChapterCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(2), page.Player.Position); // Within 2 s of B's start: back to A.
        page.Player.Seek(TimeSpan.FromSeconds(9));
        chapters.PreviousChapterCommand.Execute(null);
        Assert.Equal(TimeSpan.FromSeconds(6), page.Player.Position);
    }

    [Fact]
    public async Task APlaybackUpdateAfterTheVideoClosesIsIgnored()
    {
        var playback = new FakePlayback();
        var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();
        var version = playback.Version;
        await page.DisposeAsync();

        playback.Raise(new PlaybackUpdate(version, Milliseconds: 5000, IsPlaying: false, Error: "late"));
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 5000));

        Assert.Equal(TimeSpan.Zero, page.Player.Position);
        Assert.DoesNotContain("late", page.Status);
    }

    [Fact]
    public async Task AnUpdateFromAnEarlierPlayerIsIgnored()
    {
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();

        playback.Raise(new PlaybackUpdate(playback.Version - 1, Milliseconds: 5000));
        Assert.Equal(TimeSpan.Zero, page.Player.Position);
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 5000));
        Assert.Equal(TimeSpan.FromSeconds(5), page.Player.Position);
    }

    [Fact]
    public async Task ChangingVolumeAppliesItOnceAndMuteSilences()
    {
        var playback = new FakePlayback();
        var session = new VideoPageSession();
        await using var page = Create(new FakeMetadata(), playback, session: session);
        await page.LoadAsync();
        playback.Volumes.Clear();

        page.Player.Volume = 0.5;
        Assert.Equal([50], playback.Volumes);
        page.Player.IsMuted = true;
        Assert.Equal([50, 0], playback.Volumes);
        Assert.Equal(0.5, session.Volume);
        Assert.True(session.IsMuted);
    }

    [Fact]
    public async Task AnOutsideChangeShowsAtOnceWhenCleanAndWaitsWhenDirty()
    {
        var stored = VideoMetadata.Empty with { Title = "Beach", Chapters = [new VideoChapter(9, TimeSpan.FromSeconds(2), "Swim", "", [])] };
        var metadata = new FakeMetadata { Stored = stored };
        var record = Record(stored);
        await using var page = Create(metadata, new FakePlayback(), record);
        await page.LoadAsync();
        var row = page.Details.Chapters.Chapters[0];

        page.OnLibraryChanged(Snapshot(record with { Title = "Lake" }));
        Assert.Equal("Lake", page.Details.Title);
        Assert.False(page.Details.IsDirty);
        Assert.Same(row, page.Details.Chapters.Chapters[0]);

        page.Details.Description = "Mine";
        page.OnLibraryChanged(Snapshot(record with { Title = "River" }));
        Assert.True(page.Details.HasExternalChange);
        Assert.Equal("Lake", page.Details.Title);
        Assert.Equal("Mine", page.Details.Description);

        page.Details.RevertCommand.Execute(null);
        Assert.False(page.Details.HasExternalChange);
        Assert.Equal("River", page.Details.Title);
        Assert.Equal("", page.Details.Description);
        Assert.False(page.Details.IsDirty);
    }

    [Fact]
    public async Task OurOwnSaveArrivingFromTheLibraryChangesNothing()
    {
        var metadata = new FakeMetadata();
        var record = Record();
        await using var page = Create(metadata, new FakePlayback(), record);
        await page.LoadAsync();
        page.Details.Title = "Saved title";
        var saved = record with { Title = "Saved title" };

        // The library can publish the saved file before the save's own completion runs.
        page.OnLibraryChanged(Snapshot(saved));
        Assert.False(page.Details.HasExternalChange);
        await page.Details.SaveCommand.ExecuteAsync();
        page.OnLibraryChanged(Snapshot(saved with { Description = "" }));

        Assert.False(page.Details.HasExternalChange);
        Assert.False(page.Details.IsDirty);
        Assert.Equal("Saved title", page.Details.Title);
    }

    [Fact]
    public async Task LeavingWithUnsavedChangesAsksAndSaveLeavesOnlyAfterASuccessfulSave()
    {
        var metadata = new FakeMetadata { OnSave = () => throw new IOException("disk is full") };
        var interaction = new FakeInteraction();
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        Assert.True(await page.CanLeaveAsync(interaction));
        Assert.Equal(0, interaction.Asked);

        page.Details.Title = "Unsaved";
        interaction.Answers.Enqueue(LeaveChoice.Save);
        Assert.False(await page.CanLeaveAsync(interaction));
        interaction.Answers.Enqueue(LeaveChoice.KeepEditing);
        Assert.False(await page.CanLeaveAsync(interaction));
        interaction.Answers.Enqueue(LeaveChoice.Discard);
        Assert.True(await page.CanLeaveAsync(interaction));
        Assert.Equal(3, interaction.Asked);
    }

    [Fact]
    public async Task LeavingWaitsForASaveInFlight()
    {
        var saving = new TaskCompletionSource();
        var interaction = new FakeInteraction();
        await using var page = Create(new FakeMetadata { SaveOperation = _ => saving.Task }, new FakePlayback());
        await page.LoadAsync();
        page.Details.Title = "Saving";
        var save = page.Details.SaveCommand.ExecuteAsync();

        var leave = page.CanLeaveAsync(interaction);
        Assert.False(leave.IsCompleted);
        saving.SetResult();

        Assert.True(await leave);
        Assert.Equal(0, interaction.Asked);
        await save;
    }

    [Fact]
    public async Task FullscreenControlsHideAfterIdleWhilePlayingAndStayWhilePaused()
    {
        var time = new FakeTimeProvider();
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata(), playback, time: time);
        await page.LoadAsync();
        var fullscreen = page.Fullscreen;
        Assert.False(fullscreen.AreControlsShown);

        fullscreen.ToggleCommand.Execute(null);
        Assert.True(fullscreen.IsFullscreen);
        Assert.True(page.IsImmersive);
        Assert.True(fullscreen.AreControlsShown);
        Assert.False(page.IsInspectorShown);
        time.Advance(FullscreenViewModel.IdleDelay);
        Assert.False(fullscreen.AreControlsShown);
        Assert.True(fullscreen.IsIdle);

        fullscreen.Wake();
        time.Advance(FullscreenViewModel.IdleDelay / 2);
        fullscreen.Wake();
        time.Advance(FullscreenViewModel.IdleDelay / 2);
        Assert.True(fullscreen.AreControlsShown);
        time.Advance(FullscreenViewModel.IdleDelay / 2);
        Assert.False(fullscreen.AreControlsShown);

        playback.Raise(new PlaybackUpdate(playback.Version, IsPlaying: false));
        Assert.True(fullscreen.AreControlsShown);

        fullscreen.ExitCommand.Execute(null);
        Assert.False(fullscreen.IsFullscreen);
        Assert.False(fullscreen.ExitCommand.CanExecute(null));
        Assert.False(page.IsImmersive);
        Assert.True(page.IsInspectorShown);
        fullscreen.Wake();
        Assert.False(fullscreen.AreControlsShown);
    }

    [Fact]
    public async Task CapturedFramesAreChosenAndUnsavedOnesArePrunedOnClose()
    {
        var metadata = new FakeMetadata();
        var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        page.Player.Seek(TimeSpan.FromSeconds(3));

        await page.Details.Frames.CaptureCommand.ExecuteAsync();
        var frame = Assert.Single(page.Details.Frames.Thumbnails);
        await File.WriteAllTextAsync(frame.Thumbnail.ImagePath, "frame");
        Assert.Equal(TimeSpan.FromSeconds(3), frame.Position);
        Assert.Equal("1 / 20", page.Details.Frames.CountText);
        Assert.True(page.Details.IsDirty);

        await page.DisposeAsync();
        Assert.False(File.Exists(frame.Thumbnail.ImagePath));
    }

    [Fact]
    public async Task TheSurfaceIsShownBeforePlaybackStartsAndGoneBeforeItCloses()
    {
        var playback = new FakePlayback();
        var page = Create(new FakeMetadata(), playback);
        playback.Shown = () => page.Player.Surface;

        await page.LoadAsync();
        Assert.Same(Assert.Single(playback.Created), playback.ShownAtStart);
        await page.DisposeAsync();

        Assert.All(playback.ShownAtClose, Assert.Null);
        Assert.Equal("dispose", playback.Calls[^1]);
    }

    [Fact]
    public async Task ClosingThePageWhileThePlayerIsBeingCreatedNeverShowsIt()
    {
        var creating = new TaskCompletionSource();
        var playback = new FakePlayback { CreateGate = creating.Task };
        var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();

        var disposing = page.DisposeAsync().AsTask();
        Assert.Null(page.Player.Surface);
        creating.SetResult();
        await disposing;

        Assert.Null(page.Player.Surface);
        Assert.DoesNotContain("start", playback.Calls);
        Assert.Equal(["create", "close", "dispose"], playback.Calls);
    }

    [Fact]
    public async Task PlayWaitsForTheOpenInsteadOfStartingAnother()
    {
        var opening = new TaskCompletionSource();
        var playback = new FakePlayback { OpenGate = opening.Task };
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();

        Assert.Equal(PlaybackState.Opening, page.Player.State);
        Assert.False(page.Player.PlayPauseCommand.CanExecute(null));
        opening.SetResult();

        Assert.Equal(PlaybackState.Open, page.Player.State);
        Assert.True(page.Player.PlayPauseCommand.CanExecute(null));
        Assert.Equal(1, playback.Opens);
    }

    [Fact]
    public async Task PlayAfterAFailedStartOpensAgain()
    {
        var playback = new FakePlayback { StartError = new InvalidOperationException("The video took too long to start playing.") };
        await using var page = Create(new FakeMetadata(), playback);
        await page.LoadAsync();
        Assert.Equal(PlaybackState.Failed, page.Player.State);
        playback.StartError = null;

        await page.Player.PlayPauseCommand.ExecuteAsync();

        Assert.Equal(PlaybackState.Open, page.Player.State);
        Assert.True(page.Player.IsPlaying);
        Assert.Equal(2, playback.Opens);
    }

    [Fact]
    public async Task TimesReportedBeforeASeekLandsDoNotPullThePlayheadBack()
    {
        var time = new FakeTimeProvider();
        var playback = new FakePlayback();
        await using var page = Create(new FakeMetadata(), playback, time: time);
        await page.LoadAsync();

        page.Player.Seek(TimeSpan.FromSeconds(8));
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 2_000));
        Assert.Equal(TimeSpan.FromSeconds(8), page.Player.Position);
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 8_200));
        Assert.Equal(TimeSpan.FromMilliseconds(8_200), page.Player.Position);

        page.Player.Seek(TimeSpan.FromSeconds(9));
        time.Advance(TimeSpan.FromSeconds(3));
        playback.Raise(new PlaybackUpdate(playback.Version, Milliseconds: 3_000));
        Assert.Equal(TimeSpan.FromSeconds(3), page.Player.Position); // The seek did not take; the player is the truth.
    }

    [Fact]
    public async Task ASaveThatReleasesPlaybackFinishesBeforePlaybackReopens()
    {
        var playback = new FakePlayback();
        var page = Create(new FakeMetadata(), playback, releasePlayback: true);
        await page.LoadAsync();
        var reopening = new TaskCompletionSource();
        playback.OpenGate = reopening.Task;
        page.Details.Title = "Saved";

        await page.Details.SaveCommand.ExecuteAsync();

        Assert.False(page.Details.SaveCommand.IsRunning);
        Assert.False(page.Details.IsDirty);
        Assert.Equal(PlaybackState.Opening, page.Player.State);
        reopening.SetResult();
        Assert.Equal(PlaybackState.Open, page.Player.State);
        await page.DisposeAsync();
    }

    [Fact]
    public async Task ClosingThePageSettlesAFrameCaptureAndDeletesItsFile()
    {
        var captured = new TaskCompletionSource();
        var metadata = new FakeMetadata { CaptureGate = captured.Task };
        var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var capture = page.Details.Frames.CaptureCommand.ExecuteAsync();

        var disposing = page.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        captured.SetResult();
        await disposing;
        await capture;

        Assert.False(File.Exists(Assert.Single(metadata.Captured)));
    }

    [Fact]
    public async Task SavingCommitsWhatIsStillBeingTyped()
    {
        var metadata = new FakeMetadata { Stored = VideoMetadata.Empty with { Chapters = [new VideoChapter(9, TimeSpan.FromSeconds(2), "Swim", "", [])] } };
        await using var page = Create(metadata, new FakePlayback());
        await page.LoadAsync();
        var row = page.Details.Chapters.Chapters[0];
        page.Details.Chapters.ExpandedChapter = row;
        row.TitleText = "Swim race";
        page.Details.Tags.Draft = "lake";
        Assert.True(page.Details.IsDirty);
        Assert.True(page.Details.SaveCommand.CanExecute(null));

        await page.Details.SaveCommand.ExecuteAsync();

        Assert.Equal("Swim race", Assert.Single(metadata.Saved!.Chapters).Title);
        Assert.Equal(["lake"], metadata.Saved.Tags);
        Assert.False(page.Details.IsDirty);
    }

    [Fact]
    public async Task LeavingAsksAboutTextStillBeingTyped()
    {
        var interaction = new FakeInteraction();
        await using var page = Create(new FakeMetadata(), new FakePlayback());
        await page.LoadAsync();
        page.Details.Tags.Draft = "half-typed";

        interaction.Answers.Enqueue(LeaveChoice.Save);
        Assert.True(await page.CanLeaveAsync(interaction));

        Assert.Equal(1, interaction.Asked);
        Assert.Equal(["half-typed"], page.Details.Tags.Items);
    }

    private static Infrastructure.LibrarySnapshot Snapshot(Infrastructure.VideoRecord record) =>
        Infrastructure.LibrarySnapshot.Empty.With(videos: System.Collections.Immutable.ImmutableDictionary<Guid, Infrastructure.VideoRecord>.Empty.Add(record.Id, record));
}
