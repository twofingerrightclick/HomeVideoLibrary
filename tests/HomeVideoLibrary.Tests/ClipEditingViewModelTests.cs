using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeVideoLibrary.Tests;

/// <summary>The EDIT tab and merge page against a scripted editing service; real media is covered by ClipEditingTests.</summary>
public sealed class ClipEditingViewModelTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ScriptedEditing _editing = new();
    private readonly List<LibraryVideo> _published = [];
    private TimeSpan _playhead;
    private bool _dirty;

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

        var run = panel.TrimCommand.ExecuteAsync();
        await started.Task;
        Assert.True(panel.IsBusy);
        panel.CancelCommand.Execute(null);
        await run;

        Assert.Equal(ClipEditRun.CancelledStatus, panel.Run.Status);
        Assert.False(panel.Run.HasOutputs);
        Assert.Empty(_published);
        Assert.False(panel.IsBusy);
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

        var run = panel.TrimCommand.ExecuteAsync();
        await started.Task;
        panel.CancelCommand.Execute(null);
        committed.SetResult();
        await run;

        Assert.Equal([output], _published);
        Assert.Equal([output], panel.Run.Outputs);
        Assert.StartsWith("Saved 1 new video in the library.", panel.Run.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATrimRunsWithTheTypedRangeAndReportsWarnings()
    {
        var panel = Panel();
        panel.InText = "1:30";
        panel.OutText = "0:02:00.500";
        panel.IsPrecise = false;

        await panel.TrimCommand.ExecuteAsync();

        Assert.Equal((TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(120.5), ClipCutMode.Fast), _editing.LastTrim);
        Assert.Equal(100, panel.Run.Progress);
        Assert.False(panel.Run.IsProgressIndeterminate);
        Assert.Contains("Cuts snapped.", panel.Run.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningANewClipPassesItToTheNavigator()
    {
        var opened = new List<LibraryVideo>();
        var output = Video("Trimmed", TimeSpan.FromMinutes(2));
        _editing.Trim = (_, _) => Task.FromResult<IReadOnlyList<LibraryVideo>>([output]);
        var panel = Panel(video => { opened.Add(video); return Task.CompletedTask; });
        panel.InText = "1:00";
        await panel.TrimCommand.ExecuteAsync();

        await panel.OpenOutputCommand.ExecuteAsync(panel.Run.Outputs[0]);

        Assert.Equal([output], opened);
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
    public async Task AMergeKeepsThePageUntilItIsCancelledAndSettled()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _editing.Merge = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return []; };
        var page = Merge(2);

        _ = page.SaveCommand.ExecuteAsync();
        await started.Task;
        Assert.False(await page.CanLeaveAsync(new FakeInteraction()));
        Assert.Contains("still running", page.Run.Status, StringComparison.Ordinal);
        Assert.False(page.MoveUpCommand.CanExecute(null));

        await page.SettleForCloseAsync();
        Assert.False(page.IsBusy);
        Assert.Equal(ClipEditRun.CancelledStatus, page.Run.Status);
        Assert.True(await page.CanLeaveAsync(new FakeInteraction()));
    }

    private TrimPanelViewModel Panel(Func<LibraryVideo, Task>? open = null) =>
        new(Video("Source", TimeSpan.FromMinutes(10)), _editing, () => _playhead, Publish, open ?? (_ => Task.CompletedTask),
            () => _dirty, _time);

    private MergePageViewModel Merge(int count) =>
        new(Enumerable.Range(1, count).Select(index => Video($"Clip {index}", TimeSpan.FromMinutes(index))).ToArray(), _editing,
            Publish, _ => Task.CompletedTask, () => Task.CompletedTask, _time);

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

        public Func<IProgress<ClipEditProgress>?, CancellationToken, Task<IReadOnlyList<LibraryVideo>>>? Trim { get; set; }
        public Func<IProgress<ClipEditProgress>?, CancellationToken, Task<IReadOnlyList<LibraryVideo>>>? Merge { get; set; }

        public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
            CancellationToken cancellationToken = default)
        {
            lock (TrimPlans) TrimPlans.Add((start, end));
            return Task.FromResult(new ClipCutPlan(start, end, true, "Copied."));
        }

        public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipCutPlan(position, source.Duration, true, ""));

        public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default)
        {
            lock (MergePlans) MergePlans.Add(sources);
            return Task.FromResult(new ClipMergePlan(true, 0, $"Merge plan for {sources.Count} clips."));
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
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
