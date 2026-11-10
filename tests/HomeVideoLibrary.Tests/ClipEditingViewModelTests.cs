using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly FakeHost _host = new();
    private readonly List<LibraryVideo> _published = [];

    public ClipEditingViewModelTests() => _jobs = new JobQueue(_ => Task.CompletedTask, _time);

    [Fact]
    public async Task SettingInAndOutFromThePlayheadPlansOncePerPause()
    {
        var panel = Panel();

        _host.Playhead = TimeSpan.FromSeconds(10);
        panel.SetInCommand.Execute(null);
        _host.Playhead = TimeSpan.FromSeconds(20);
        panel.SetOutCommand.Execute(null);
        await PlannedAsync(panel.TrimPlan);

        Assert.Equal([(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20))], _editing.TrimPlans);
        Assert.Equal((TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)), (panel.RangeStart, panel.RangeEnd));

        _host.Playhead = TimeSpan.FromSeconds(30);
        panel.SetOutCommand.Execute(null);
        _time.Advance(PlanReadout.Delay - TimeSpan.FromMilliseconds(1));
        Assert.Single(_editing.TrimPlans);
        await PlannedAsync(panel.TrimPlan, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, _editing.TrimPlans.Count);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1.3, false)]
    [InlineData(60, true)]
    public async Task TheTrimButtonFollowsTheCorePlansRenderRule(double reencodedSeconds, bool rendersVideo)
    {
        _editing.TrimPlan = (start, end) => new ClipCutPlan(start, end, reencodedSeconds == 0, "")
            { ReencodedDuration = TimeSpan.FromSeconds(reencodedSeconds) };
        var panel = Panel();

        panel.InText = "1:00";
        panel.OutText = "2:00";
        await PlannedAsync(panel.TrimPlan);

        var ready = Assert.IsType<PlanState.Ready>(panel.TrimPlan.State);
        Assert.Equal(rendersVideo, ready.Plan.RendersVideo);
        Assert.Equal(rendersVideo ? "RENDER" : "SAVE", panel.TrimPlan.ActionLabel);
        Assert.True(panel.TrimCommand.CanExecute(null));
    }

    [Fact]
    public async Task NothingStartsUntilAPlanIsReadyAndAFailedPlanSaysWhy()
    {
        var panel = Panel();
        Assert.IsType<PlanState.Idle>(panel.TrimPlan.State);
        Assert.False(panel.TrimCommand.CanExecute(null));
        var changes = 0;
        panel.TrimCommand.CanExecuteChanged += (_, _) => changes++;

        _editing.TrimPlan = (_, _) => throw new ClipRangeException("There is no keyframe near that position.");
        panel.OutText = "2:00";
        Assert.IsType<PlanState.Planning>(panel.TrimPlan.State);
        Assert.False(panel.TrimCommand.CanExecute(null));
        await PlannedAsync(panel.TrimPlan);

        Assert.Equal(new PlanState.Failed("There is no keyframe near that position."), panel.TrimPlan.State);
        Assert.Equal("There is no keyframe near that position.", panel.TrimPlan.Summary);
        Assert.Equal("SAVE", panel.TrimPlan.ActionLabel);
        Assert.False(panel.TrimCommand.CanExecute(null));
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task AnOlderPlanNeverReplacesANewerOne()
    {
        var slow = new TaskCompletionSource<ClipCutPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = Panel();
        _editing.PlanTrimAsyncOverride = (start, end) => start == TimeSpan.FromSeconds(1) ? slow.Task : Task.FromResult(new ClipCutPlan(start, end, true, "newer"));

        panel.InText = "1";
        _time.Advance(PlanReadout.Delay);
        panel.InText = "2";
        await PlannedAsync(panel.TrimPlan);
        slow.SetResult(new ClipCutPlan(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), true, "older"));
        await Task.Delay(50);

        Assert.EndsWith("newer", Assert.IsType<PlanState.Ready>(panel.TrimPlan.State).Plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingWhileAProbeRunsShowsNothingLate()
    {
        var probe = new TaskCompletionSource<ClipCutPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.PlanTrimAsyncOverride = (_, _) => probe.Task;
        var panel = Panel();
        panel.OutText = "2:00";
        _time.Advance(PlanReadout.Delay);
        await Task.Delay(20);

        panel.Dispose();
        probe.SetResult(new ClipCutPlan(TimeSpan.Zero, TimeSpan.FromMinutes(2), true, ""));
        await panel.TrimPlan.Completion;

        Assert.IsNotType<PlanState.Ready>(panel.TrimPlan.State);
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

        page.SelectedSource = second;
        page.MoveUpCommand.Execute(null);
        await PlannedAsync(page.Plan);
        Assert.Equal("SAVE", page.Plan.ActionLabel);
    }

    [Fact]
    public async Task AMergeThatOnlyConvertsSoundSaves()
    {
        var page = Merge(2);
        _editing.MergePlan = sources => new ClipMergePlan(false, 1, "Converts sound.")
        {
            Clips = [new(false, false, sources[0].Duration, null), new(false, true, sources[1].Duration, "its sound differs")],
        };
        await page.LoadAsync();
        await page.Plan.Completion;

        Assert.False(Assert.IsType<PlanState.Ready>(page.Plan.State).Plan.RendersVideo);
        Assert.Equal("SAVE", page.Plan.ActionLabel);
    }

    [Fact]
    public async Task PressingSaveMergedTwiceQueuesOneMerge()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Merge = async (_, _) => { await release.Task; return []; };
        var page = Merge(2);
        Assert.False(page.SaveCommand.CanExecute(null)); // Not planned yet.
        await page.LoadAsync();
        await page.Plan.Completion;

        page.SaveCommand.Execute(null);
        page.SaveCommand.Execute(null);

        Assert.Single(_jobs.Jobs);
        release.SetResult();
        await page.Job!.Completion;
        Assert.False(page.SaveCommand.CanExecute(null));
    }

    [Fact]
    public Task AQueuedMergeTakesTheFormAwayAndAFinishedOneLinksToTheNewVideo() => UiThread.Run(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Video("Summer 1994", TimeSpan.FromMinutes(3));
        _editing.Merge = async (_, _) => { await release.Task; return [output]; };
        var opened = new List<JobViewModel>();
        var page = Merge(2, new JobQueue(job => { opened.Add(job); return Task.CompletedTask; }, _time));
        await page.LoadAsync();
        await page.Plan.Completion;
        page.SelectedSource = page.Sources[0];
        Assert.True(page.IsEditing);

        page.SaveCommand.Execute(null);

        Assert.False(page.IsEditing);
        Assert.False(page.IsMerged);
        Assert.False(page.SaveCommand.CanExecute(null));
        Assert.False(page.MoveDownCommand.CanExecute(null));

        release.SetResult();
        await page.Job!.Completion;
        await UiThread.Settle();

        Assert.False(page.IsEditing);
        Assert.True(page.IsMerged);
        Assert.Equal("Summer 1994", page.MergedTitle);
        Assert.False(page.SaveCommand.CanExecute(null));
        page.Job.OpenCommand.Execute(null);
        await UiThread.Settle();
        Assert.Equal([page.Job], opened);
        Assert.Equal([output.Id], page.Job.Outcome!.Outputs);
    });

    [Theory]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Cancelled)]
    public Task AMergeThatFailsOrIsCancelledGivesTheFormBackToRetry(JobState ending) => UiThread.Run(async () =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Merge = async (_, token) =>
        {
            started.SetResult();
            if (ending == JobState.Failed) throw new InvalidOperationException("The disk is full.");
            await Task.Delay(Timeout.Infinite, token);
            return [];
        };
        var page = Merge(2);
        await page.LoadAsync();
        await page.Plan.Completion;

        page.SaveCommand.Execute(null);
        var job = page.Job!;
        await started.Task;
        if (ending == JobState.Cancelled) job.Cancel();
        await job.Completion;
        await UiThread.Settle();

        Assert.Equal(ending, job.State);
        Assert.Equal(ending == JobState.Failed ? "The disk is full." : "Cancelled", job.StatusText);
        Assert.True(page.IsEditing);
        Assert.False(page.IsMerged);
        Assert.Null(page.MergedTitle);
        Assert.True(page.SaveCommand.CanExecute(null));
    });

    [Fact]
    public async Task AnOutPointBeforeTheInPointShowsAMessageAndDoesNotPlan()
    {
        var panel = Panel();

        panel.InText = "0:30";
        panel.OutText = "10";
        await PlannedAsync(panel.TrimPlan, TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(30), panel.In);
        Assert.Equal("The out point must come after the in point.", panel.RangeError);
        Assert.Equal(new PlanState.Idle("The out point must come after the in point."), panel.TrimPlan.State);
        Assert.Empty(_editing.TrimPlans);
        Assert.False(panel.TrimCommand.CanExecute(null));
    }

    [Fact]
    public void AnInPointAtTheEndIsRejectedWithItsReason()
    {
        var panel = Panel();

        panel.InText = "10:00";

        Assert.Equal("The in point is at or past the end of the video (0:10:00.000).", panel.RangeError);
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

        _host.Playhead = TimeSpan.FromSeconds(5);
        panel.SetInCommand.Execute(null);
        Assert.Equal("0:00:05.000", panel.InText);
        Assert.Null(panel.InTextError);
    }

    [Fact]
    public async Task ThePageBlockingEditsDisablesTrimAndSplit()
    {
        _host.IsEditShown = true;
        _host.Playhead = TimeSpan.FromMinutes(1);
        var panel = Panel();
        panel.SetInCommand.Execute(null);
        await PlannedAsync(panel.TrimPlan);
        await panel.SplitPlan.Completion;
        Assert.True(panel.TrimCommand.CanExecute(null));
        var changes = 0;
        panel.TrimCommand.CanExecuteChanged += (_, _) => changes++;

        _host.EditBlocker = "Save or revert your changes first";
        Assert.False(panel.TrimCommand.CanExecute(null));
        Assert.False(panel.SplitCommand.CanExecute(null));
        Assert.Equal("Save or revert your changes first", panel.EditBlocker);

        _host.EditBlocker = null;
        Assert.Equal(2, changes);
        Assert.True(panel.TrimCommand.CanExecute(null));
        Assert.True(panel.SplitCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSplitPointIsTakenOnlyWhileTheTabShowsAndThePlayheadRests()
    {
        var panel = Panel();
        _host.Playhead = TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.Zero, panel.SplitAt); // EDIT is not shown.

        _host.IsEditShown = true;
        Assert.Equal(TimeSpan.FromMinutes(2), panel.SplitAt);
        _host.IsPlayheadResting = false;
        _host.Playhead = TimeSpan.FromMinutes(3);
        Assert.Equal(TimeSpan.FromMinutes(2), panel.SplitAt); // Playing: ticks are ignored.
        _host.IsPlayheadResting = true;
        Assert.Equal(TimeSpan.FromMinutes(3), panel.SplitAt);
        await PlannedAsync(panel.SplitPlan);

        Assert.Equal([TimeSpan.FromMinutes(3)], _editing.SplitPlans);
        Assert.True(panel.SplitCommand.CanExecute(null));
        Assert.Equal("SAVE", panel.SplitPlan.ActionLabel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public void ThereIsNothingToSplitAtEitherEnd(double seconds)
    {
        _host.IsEditShown = true;
        _host.Playhead = TimeSpan.FromSeconds(seconds);
        var panel = Panel();

        Assert.Equal(new PlanState.Idle("Pause where the second part should start."), panel.SplitPlan.State);
        Assert.False(panel.SplitCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATrimTakesItsInputsAndTheCurrentRecordWhenQueued()
    {
        var panel = Panel();
        panel.InText = "1:30";
        panel.OutText = "0:02:00.500";
        panel.IsPrecise = false;
        await PlannedAsync(panel.TrimPlan);
        _host.Source = _host.Source with { Title = "Renamed after saving" };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Trim = async (_, _) => { await release.Task; return []; };

        panel.TrimCommand.Execute(null);
        panel.OutText = "5:00";
        panel.OutputTitle = "Changed after queueing";
        release.SetResult();
        await panel.Job!.Completion;

        var trim = _editing.LastTrim;
        Assert.Equal((TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120.5), ClipCutMode.Fast), (trim.Start, trim.End, trim.Mode));
        Assert.Equal(("Renamed after saving", ""), (trim.Source.Title, trim.Title));
        Assert.Equal([_host.Source.Id], panel.Job.Sources);
    }

    [Fact]
    public async Task ATrimReportsWarningsInItsSummary()
    {
        var panel = Panel();
        panel.OutText = "2:00";
        await PlannedAsync(panel.TrimPlan);

        panel.TrimCommand.Execute(null);
        await panel.Job!.Completion;

        Assert.Equal(JobState.Succeeded, panel.Job.State);
        Assert.Contains("Cuts snapped.", panel.Job.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailingTrimShowsItsMessage()
    {
        _editing.Trim = (_, _) => throw new InvalidOperationException("mkvmerge could not write the clip.");
        var panel = Panel();
        panel.OutText = "2:00";
        await PlannedAsync(panel.TrimPlan);

        panel.TrimCommand.Execute(null);
        await panel.Job!.Completion;

        Assert.Equal(JobState.Failed, panel.Job.State);
        Assert.Equal("mkvmerge could not write the clip.", panel.Job.StatusText);
        Assert.True(panel.TrimCommand.CanExecute(null)); // The failed job no longer holds the button.
    }

    [Fact]
    public async Task CancellingARunningTrimPublishesNothing()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Trim = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
        var panel = Panel();
        panel.InText = "1:00";
        await PlannedAsync(panel.TrimPlan);

        panel.TrimCommand.Execute(null);
        var job = Assert.IsType<JobViewModel>(panel.Job);
        Assert.False(panel.TrimCommand.CanExecute(null)); // The same trim cannot be queued twice.
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
        await PlannedAsync(panel.TrimPlan);

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
    public async Task ASplitWaitsInTheRenderQueueBehindATrim()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Trim = async (_, _) => { await release.Task; return []; };
        _host.IsEditShown = true;
        var panel = Panel();
        panel.InText = "1:00";
        await PlannedAsync(panel.TrimPlan);

        panel.TrimCommand.Execute(null);
        var first = panel.Job!;
        _host.Playhead = TimeSpan.FromMinutes(5);
        await PlannedAsync(panel.SplitPlan);
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
        await PlannedAsync(page.Plan, TimeSpan.FromMilliseconds(1));

        var plan = Assert.Single(_editing.MergePlans);
        Assert.Equal(page.Sources.Select(source => source.Video), plan);
        Assert.Same(first, page.Sources[1]);
        Assert.Same(first, page.SelectedSource);
    }

    [Fact]
    public async Task LeavingTheMergePageLeavesItsJobRunning()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Video("Merged", TimeSpan.FromMinutes(3));
        _editing.Merge = async (_, _) => { await release.Task; return [output]; };
        var page = Merge(2);
        await page.LoadAsync();
        await page.Plan.Completion;

        page.SaveCommand.Execute(null);
        var job = page.Job!;
        Assert.Equal(page.Sources.Select(source => source.Video.Id), job.Sources);
        Assert.True(await page.CanLeaveAsync(new FakeInteraction()));
        await page.DisposeAsync();
        Assert.False(job.IsFinished);

        release.SetResult();
        await job.Completion;
        Assert.Equal([output], _published);
        Assert.True(job.CanOpen);
    }

    private async Task PlannedAsync(PlanReadout readout, TimeSpan? wait = null)
    {
        _time.Advance(wait ?? PlanReadout.Delay);
        await readout.Completion;
    }

    private TrimPanelViewModel Panel() => new(new ClipEdits(_editing, _jobs, Publish), _host, _time);

    private MergePageViewModel Merge(int count, JobQueue? jobs = null) =>
        new(Enumerable.Range(1, count).Select(index => Video($"Clip {index}", TimeSpan.FromMinutes(index))).ToArray(),
            new ClipEdits(_editing, jobs ?? _jobs, Publish), () => Task.CompletedTask, _time);

    private Task Publish(IReadOnlyList<LibraryVideo> outputs)
    {
        _published.AddRange(outputs);
        return Task.CompletedTask;
    }

    private static LibraryVideo Video(string name, TimeSpan duration) =>
        new(Guid.NewGuid(), $"Videos/{name}.mkv", name, duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>A page around the panel: a 10 minute video, paused, with the EDIT tab hidden until a test shows it.</summary>
    private sealed class FakeHost : IEditHost
    {
        private LibraryVideo _source = Video("Source", TimeSpan.FromMinutes(10));
        private TimeSpan _playhead;
        private bool _isPlayheadResting = true, _isEditShown;
        private string? _editBlocker;

        public event PropertyChangedEventHandler? PropertyChanged;

        public LibraryVideo Source { get => _source; set => Set(ref _source, value); }
        public TimeSpan Playhead { get => _playhead; set => Set(ref _playhead, value); }
        public bool IsPlayheadResting { get => _isPlayheadResting; set => Set(ref _isPlayheadResting, value); }
        public bool IsEditShown { get => _isEditShown; set => Set(ref _isEditShown, value); }
        public string? EditBlocker { get => _editBlocker; set => Set(ref _editBlocker, value); }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private sealed class ScriptedEditing : IClipEditingService
    {
        public List<(TimeSpan Start, TimeSpan End)> TrimPlans { get; } = [];
        public List<TimeSpan> SplitPlans { get; } = [];
        public List<IReadOnlyList<LibraryVideo>> MergePlans { get; } = [];
        public (TimeSpan Start, TimeSpan End, ClipCutMode Mode, LibraryVideo Source, string Title) LastTrim { get; private set; }

        public Func<TimeSpan, TimeSpan, ClipCutPlan> TrimPlan { get; set; } = (start, end) => new ClipCutPlan(start, end, true, "Copied.");
        public Func<TimeSpan, TimeSpan, Task<ClipCutPlan>>? PlanTrimAsyncOverride { get; set; }
        public Func<IReadOnlyList<LibraryVideo>, ClipMergePlan> MergePlan { get; set; } =
            sources => new ClipMergePlan(true, 0, $"Merge plan for {sources.Count} clips.");
        public Func<IProgress<ClipEditProgress>?, CancellationToken, Task<IReadOnlyList<LibraryVideo>>>? Trim { get; set; }
        public Func<IProgress<ClipEditProgress>?, CancellationToken, Task<IReadOnlyList<LibraryVideo>>>? Merge { get; set; }

        public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
            CancellationToken cancellationToken = default)
        {
            lock (TrimPlans) TrimPlans.Add((start, end));
            return PlanTrimAsyncOverride is { } plan ? plan(start, end) : Task.FromResult(TrimPlan(start, end));
        }

        public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default)
        {
            lock (SplitPlans) SplitPlans.Add(position);
            return Task.FromResult(new ClipCutPlan(position, source.Duration, true, ""));
        }

        public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default)
        {
            lock (MergePlans) MergePlans.Add(sources);
            return Task.FromResult(MergePlan(sources));
        }

        public Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise)
        {
            LastTrim = (start, end, mode, source, title);
            if (Trim is { } trim) return trim(progress, cancellationToken);
            progress?.Report(new ClipEditProgress("Writing", 0.5, "Cuts snapped."));
            return Task.FromResult<IReadOnlyList<LibraryVideo>>([source]);
        }

        public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Merge is { } merge ? merge(progress, cancellationToken) : Task.FromResult<IReadOnlyList<LibraryVideo>>([sources[0]]);

        public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LibraryVideo>>([]);
    }
}
