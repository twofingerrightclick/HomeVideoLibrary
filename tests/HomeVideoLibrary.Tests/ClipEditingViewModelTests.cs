using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeVideoLibrary.Tests;

/// <summary>The EDIT tab and merge page against a scripted editing service; real media is covered by ClipEditingTests.</summary>
public sealed class ClipEditingViewModelTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ScriptedEditing _editing = new();
    private readonly JobQueue _jobs;
    private readonly List<LibraryVideo> _published = [];
    private TimeSpan _playhead;
    private bool _dirty;

    public ClipEditingViewModelTests() => _jobs = new JobQueue(_ => Task.CompletedTask, _time);

    [Fact]
    public async Task SettingInAndOutFromThePlayheadPlansOncePerPause()
    {
        var panel = Panel();

        _playhead = TimeSpan.FromSeconds(10);
        panel.SetInCommand.Execute(null);
        _playhead = TimeSpan.FromSeconds(20);
        panel.SetOutCommand.Execute(null);
        _time.Advance(PlanReadout.Delay);
        await panel.TrimPlan.Completion;

        Assert.Equal([(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))], _editing.TrimPlans);
        Assert.StartsWith("Keeps 0:00:10.000 – 0:00:20.000 · lossless.", panel.TrimPlan.Summary, StringComparison.Ordinal);
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)), (panel.RangeStart, panel.RangeEnd));

        _playhead = TimeSpan.FromSeconds(30);
        panel.SetOutCommand.Execute(null);
        _time.Advance(PlanReadout.Delay - TimeSpan.FromMilliseconds(1));
        Assert.Single(_editing.TrimPlans);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await panel.TrimPlan.Completion;
        Assert.Equal(2, _editing.TrimPlans.Count);
    }

    [Theory]
    [InlineData(0, "SAVE", "copying the picture and sound exactly")]
    [InlineData(1.3, "SAVE", "The 1.3 s next to the cuts is re-encoded")]
    [InlineData(60, "RENDER", "all of it is re-encoded: 1 min 0 s of video to render")]
    public async Task TheTrimButtonSavesCopiesAndSmartCutsAndRendersFullReencodes(double reencodedSeconds, string label, string explanation)
    {
        _editing.TrimPlan = (start, end) => new ClipCutPlan(start, end, reencodedSeconds == 0, "")
            { ReencodedDuration = TimeSpan.FromSeconds(reencodedSeconds) };
        var panel = Panel();

        panel.InText = "1:00";
        panel.OutText = "2:00";
        _time.Advance(PlanReadout.Delay);
        await panel.TrimPlan.Completion;

        Assert.Equal(label, panel.TrimPlan.ActionLabel);
        Assert.Contains(explanation, panel.TrimPlan.Details, StringComparison.Ordinal);
        Assert.Contains("0:01:00.000 – 0:02:00.000", panel.TrimPlan.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMergeButtonFollowsThePlanAndExplainsEachReencodedClip()
    {
        var page = Merge(3);
        var second = page.Sources[1];
        _editing.MergePlan = sources => sources[1] == second.Video
            ? new ClipMergePlan(false, 1, "Re-encodes 1 of 3 clips.")
            {
                Clips =
                [
                    new(false, false, sources[0].Duration, null),
                    new(true, false, sources[1].Duration, "it is 720×480 and the merged video is 1920×1080"),
                    new(false, false, sources[2].Duration, null),
                ],
            }
            : new ClipMergePlan(true, 0, "Lossless.");
        await page.LoadAsync();
        await page.Plan.Completion;

        Assert.Equal("RENDER", page.Plan.ActionLabel);
        Assert.Contains("Clip 2 (Clip 2) is re-encoded because it is 720×480 and the merged video is 1920×1080.", page.Plan.Details,
            StringComparison.Ordinal);
        Assert.Contains("2 min 0 s of video to render", page.Plan.Details, StringComparison.Ordinal);

        page.SelectedSource = second;
        page.MoveUpCommand.Execute(null);
        _time.Advance(PlanReadout.Delay);
        await page.Plan.Completion;
        Assert.Equal("SAVE", page.Plan.ActionLabel);
    }

    [Fact]
    public async Task AnOutPointBeforeTheInPointShowsAMessageAndDoesNotPlan()
    {
        var panel = Panel();

        panel.InText = "0:30";
        panel.OutText = "10";
        _time.Advance(TimeSpan.FromSeconds(1));
        await panel.TrimPlan.Completion;

        Assert.Equal(TimeSpan.FromSeconds(30), panel.In);
        Assert.NotNull(panel.RangeError);
        Assert.Empty(_editing.TrimPlans);
        Assert.False(panel.TrimCommand.CanExecute(null));
    }

    [Fact]
    public void TextThatIsNotATimeKeepsThePointAndIsShownForCorrection()
    {
        var panel = Panel();
        panel.InText = "1:30";

        panel.InText = "1:3x";

        Assert.Equal(TimeSpan.FromSeconds(90), panel.In);
        Assert.Equal("1:3x", panel.InText);
        Assert.NotNull(panel.InTextError);
        Assert.False(panel.TrimCommand.CanExecute(null));

        _playhead = TimeSpan.FromSeconds(5);
        panel.SetInCommand.Execute(null);
        Assert.Equal("0:00:05.000", panel.InText);
        Assert.Null(panel.InTextError);
    }

    [Fact]
    public void TrimAndSplitAreDisabledWhileTheDetailsHaveUnsavedChanges()
    {
        _dirty = true;
        _playhead = TimeSpan.FromMinutes(1);
        var panel = Panel();
        panel.SetInCommand.Execute(null);
        var changes = 0;
        panel.TrimCommand.CanExecuteChanged += (_, _) => changes++;

        Assert.False(panel.TrimCommand.CanExecute(null));
        Assert.False(panel.SplitCommand.CanExecute(null));
        Assert.Equal(TrimPanelViewModel.UnsavedDetailsMessage, panel.UnsavedDetailsHint);

        _dirty = false;
        panel.DetailsDirtyChanged();
        Assert.Equal(1, changes);
        Assert.True(panel.TrimCommand.CanExecute(null));
        Assert.True(panel.SplitCommand.CanExecute(null));
        Assert.Null(panel.UnsavedDetailsHint);
    }

    [Fact]
    public async Task CancellingARunningTrimPublishesNothing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Trim = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
        var panel = Panel();
        panel.InText = "1:00";

        panel.TrimCommand.Execute(null);
        var job = Assert.IsType<JobViewModel>(panel.Job);
        await started.Task;
        job.CancelCommand.Execute(null);
        await job.Completion;

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task OutputsCommittedBeforeACancelAreStillPublished()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Video("Trimmed", TimeSpan.FromMinutes(2));
        // The service is past its commit point: it ignores the token and returns the new video.
        _editing.Trim = async (_, _) => { started.SetResult(); await committed.Task; return [output]; };
        var panel = Panel();
        panel.InText = "1:00";

        panel.TrimCommand.Execute(null);
        var job = panel.Job!;
        await started.Task;
        job.Cancel();
        committed.SetResult();
        await job.Completion;

        Assert.Equal([output], _published);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal([output.Id], job.Outcome!.Outputs);
        Assert.StartsWith("Saved 1 new video.", job.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrimRunsWithTheTypedRangeAndReportsWarnings()
    {
        var panel = Panel();
        panel.InText = "1:30";
        panel.OutText = "0:02:00.500";
        panel.IsPrecise = false;

        panel.TrimCommand.Execute(null);
        await panel.Job!.Completion;

        Assert.Equal((TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120.5), ClipCutMode.Fast), _editing.LastTrim);
        Assert.Equal(JobState.Succeeded, panel.Job.State);
        Assert.Contains("Cuts snapped.", panel.Job.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondEditWaitsInTheRenderQueue()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Trim = async (_, _) => { await release.Task; return []; };
        var panel = Panel();
        panel.InText = "1:00";

        panel.TrimCommand.Execute(null);
        var first = panel.Job!;
        _playhead = TimeSpan.FromMinutes(5);
        panel.PlayheadMoved();
        panel.SplitCommand.Execute(null);
        var second = panel.Job!;

        Assert.NotSame(first, second);
        Assert.Equal("Queued (#1)", second.StatusText);
        release.SetResult();
        await first.Completion;
        Assert.Equal(JobState.Succeeded, first.State);
    }

    [Fact]
    public async Task ReorderingAMergePlansOnceAfterThePause()
    {
        var page = Merge(3);
        var first = page.Sources[0];
        page.SelectedSource = first;

        page.MoveDownCommand.Execute(null);
        page.MoveDownCommand.Execute(null);
        page.MoveUpCommand.Execute(null);
        _time.Advance(PlanReadout.Delay - TimeSpan.FromMilliseconds(1));
        Assert.Empty(_editing.MergePlans);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        await page.Plan.Completion;

        var plan = Assert.Single(_editing.MergePlans);
        Assert.Equal(page.Sources.Select(source => source.Video), plan);
        Assert.Same(first, page.Sources[1]);
        Assert.Same(first, page.SelectedSource);
        Assert.Equal("Merge plan for 3 clips.", page.Plan.Summary);
    }

    [Fact]
    public async Task LeavingTheMergePageLeavesItsJobRunning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Video("Merged", TimeSpan.FromMinutes(3));
        _editing.Merge = async (_, _) => { await release.Task; return [output]; };
        var page = Merge(2);

        page.SaveCommand.Execute(null);
        var job = page.Job!;
        Assert.True(await page.CanLeaveAsync(new FakeInteraction()));
        await page.DisposeAsync();
        Assert.False(job.IsFinished);

        release.SetResult();
        await job.Completion;
        Assert.Equal([output], _published);
        Assert.True(job.CanOpen);
    }

    private TrimPanelViewModel Panel() =>
        new(Video("Source", TimeSpan.FromMinutes(10)), new ClipEdits(_editing, _jobs, Publish), () => _playhead, () => _dirty, _time);

    private MergePageViewModel Merge(int count) =>
        new(Enumerable.Range(1, count).Select(index => Video($"Clip {index}", TimeSpan.FromMinutes(index))).ToArray(),
            new ClipEdits(_editing, _jobs, Publish), () => Task.CompletedTask, _time);

    private Task Publish(IReadOnlyList<LibraryVideo> outputs)
    {
        _published.AddRange(outputs);
        return Task.CompletedTask;
    }

    private static LibraryVideo Video(string name, TimeSpan duration) =>
        new(Guid.NewGuid(), $"Videos/{name}.mkv", name, duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class ScriptedEditing : IClipEditingService
    {
        public List<(TimeSpan Start, TimeSpan End)> TrimPlans { get; } = [];
        public List<IReadOnlyList<LibraryVideo>> MergePlans { get; } = [];
        public (TimeSpan Start, TimeSpan End, ClipCutMode Mode) LastTrim { get; private set; }

        public Func<TimeSpan, TimeSpan, ClipCutPlan> TrimPlan { get; set; } = (start, end) => new ClipCutPlan(start, end, true, "Copied.");
        public Func<IReadOnlyList<LibraryVideo>, ClipMergePlan> MergePlan { get; set; } =
            sources => new ClipMergePlan(true, 0, $"Merge plan for {sources.Count} clips.");
        public Func<IProgress<ClipEditProgress>?, CancellationToken, Task<IReadOnlyList<LibraryVideo>>>? Trim { get; set; }
        public Func<IProgress<ClipEditProgress>?, CancellationToken, Task<IReadOnlyList<LibraryVideo>>>? Merge { get; set; }

        public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
            CancellationToken cancellationToken = default)
        {
            lock (TrimPlans) TrimPlans.Add((start, end));
            return Task.FromResult(TrimPlan(start, end));
        }

        public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipCutPlan(position, source.Duration, true, ""));

        public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default)
        {
            lock (MergePlans) MergePlans.Add(sources);
            return Task.FromResult(MergePlan(sources));
        }

        public Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise)
        {
            LastTrim = (start, end, mode);
            if (Trim is { } trim) return trim(progress, cancellationToken);
            progress?.Report(new ClipEditProgress("Writing", 0.5, "Cuts snapped."));
            return Task.FromResult<IReadOnlyList<LibraryVideo>>([source]);
        }

        public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Merge is { } merge ? merge(progress, cancellationToken) : Task.FromResult<IReadOnlyList<LibraryVideo>>([sources[0]]);

        public Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibraryVideo>>([]);
    }
}
