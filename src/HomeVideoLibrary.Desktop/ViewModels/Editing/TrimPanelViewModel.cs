using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>The video page's EDIT tab: trim to an in/out range or split at the playhead, each saved as new videos by a
/// render job. Times are absolute Matroska timestamps, which equal the displayed playback time for imported videos.</summary>
public sealed class TrimPanelViewModel : ViewModelBase, IDisposable
{
    public const string UnsavedDetailsMessage = "Save or revert your changes first";

    private readonly ClipEdits _edits;
    private readonly Func<TimeSpan> _playhead;
    private readonly Func<bool> _hasUnsavedDetails;
    private TimeSpan? _in, _out;
    // Text the user committed that is not a time; kept so it can be corrected, and cleared by any valid input.
    private string? _rejectedIn, _rejectedOut;
    private TimeSpan _splitAt;
    private bool _isPrecise = true;
    private string _outputTitle;
    private JobViewModel? _job;

    /// <param name="playhead">The player's current position, read when I/O is pressed and when the page reports a move.</param>
    /// <param name="edits">Plans the edits and runs them as render jobs, which keep running if the page closes.</param>
    /// <param name="hasUnsavedDetails">Whether the page has unsaved metadata edits. New clips copy the file's saved
    /// metadata, so edits are blocked until they are saved or reverted. Call <see cref="DetailsDirtyChanged"/> when it changes.</param>
    /// <param name="time">Clock for the plan delay; tests pass a fake.</param>
    public TrimPanelViewModel(LibraryVideo source, ClipEdits edits, Func<TimeSpan> playhead, Func<bool> hasUnsavedDetails,
        TimeProvider? time = null)
    {
        Source = source; _edits = edits; _playhead = playhead; _hasUnsavedDetails = hasUnsavedDetails;
        _outputTitle = source.Title.Length > 0 ? source.Title : source.DisplayName;
        TrimPlan = new PlanReadout(time ?? TimeProvider.System);
        SplitPlan = new PlanReadout(time ?? TimeProvider.System);
        SetInCommand = new RelayCommand(() => SetInPoint(_playhead(), null));
        SetOutCommand = new RelayCommand(() => SetOutPoint(_playhead(), null));
        ClearCommand = new RelayCommand(Clear, () => HasRange || _rejectedIn is not null || _rejectedOut is not null);
        TrimCommand = new RelayCommand(Trim, () => CanTrim);
        SplitCommand = new RelayCommand(Split, () => CanSplit);
        _splitAt = playhead();
        RefreshTrimPlan();
        RefreshSplitPlan();
    }

    public LibraryVideo Source { get; }

    /// <summary>Where the kept range starts; null keeps from the beginning.</summary>
    public TimeSpan? In
    {
        get => _in;
        private set { if (SetProperty(ref _in, value)) RangeChanged(); }
    }

    /// <summary>Where the kept range ends; null keeps to the end.</summary>
    public TimeSpan? Out
    {
        get => _out;
        private set { if (SetProperty(ref _out, value)) RangeChanged(); }
    }

    /// <summary>The in point as text. Setting it commits: a time sets <see cref="In"/>, empty text clears it, and
    /// anything else is kept with <see cref="InTextError"/>. Bind it so the view writes only when editing ends.</summary>
    public string InText
    {
        get => _rejectedIn ?? Format(In);
        set { var (time, rejected) = Parse(value); SetInPoint(time, rejected); }
    }

    public string OutText
    {
        get => _rejectedOut ?? Format(Out);
        set { var (time, rejected) = Parse(value); SetOutPoint(time, rejected); }
    }

    public string? InTextError => _rejectedIn is null ? null : "Enter the in point as h:mm:ss.fff, m:ss or seconds.";
    public string? OutTextError => _rejectedOut is null ? null : "Enter the out point as h:mm:ss.fff, m:ss or seconds.";

    public bool HasRange => In is not null || Out is not null;

    /// <summary>The range the trim keeps, for the scrub bar; null until an in or out point is set.</summary>
    public TimeSpan? RangeStart => HasRange ? In ?? TimeSpan.Zero : null;
    public TimeSpan? RangeEnd => HasRange ? Out ?? Source.Duration : null;

    public string? RangeError => RangeStart is { } start && RangeEnd is { } end && end <= start
        ? "The out point must come after the in point." : null;

    /// <summary>Frame-accurate cuts (re-encoding a fraction of a second at each cut) instead of keyframe-snapped copies.</summary>
    public bool IsPrecise
    {
        get => _isPrecise;
        set { if (SetProperty(ref _isPrecise, value)) RefreshTrimPlan(); }
    }

    public string OutputTitle { get => _outputTitle; set => SetProperty(ref _outputTitle, value); }

    /// <summary>Where a split would start the second part: the playhead when the page last reported it.</summary>
    public TimeSpan SplitAt
    {
        get => _splitAt;
        private set
        {
            if (!SetProperty(ref _splitAt, value)) return;
            RefreshSplitPlan();
            UpdateCommands();
        }
    }

    /// <summary>What the trim will really keep, whether it copies or renders, and why; refreshed after input pauses.</summary>
    public PlanReadout TrimPlan { get; }

    public PlanReadout SplitPlan { get; }

    /// <summary>The latest edit started here, shown with its progress, CANCEL and OPEN; null until one starts.</summary>
    public JobViewModel? Job { get => _job; private set => SetProperty(ref _job, value); }

    /// <summary>Why saving is blocked by the page's metadata edits, or null.</summary>
    public string? UnsavedDetailsHint => _hasUnsavedDetails() ? UnsavedDetailsMessage : null;

    public RelayCommand SetInCommand { get; }
    public RelayCommand SetOutCommand { get; }
    public RelayCommand ClearCommand { get; }
    /// <summary>Queues a render job for the kept range; another edit may already be running or queued.</summary>
    public RelayCommand TrimCommand { get; }
    public RelayCommand SplitCommand { get; }

    private bool CanTrim => !_hasUnsavedDetails() && HasRange && RangeError is null && _rejectedIn is null && _rejectedOut is null;
    private bool CanSplit => !_hasUnsavedDetails() && IsInside(SplitAt);
    private ClipCutMode Mode => IsPrecise ? ClipCutMode.Precise : ClipCutMode.Fast;

    /// <summary>The page calls this when the playhead moves; the split plan follows once it rests.</summary>
    public void PlayheadMoved() => SplitAt = _playhead();

    /// <summary>The page calls this when its unsaved-details state changes.</summary>
    public void DetailsDirtyChanged()
    {
        OnPropertyChanged(nameof(UnsavedDetailsHint));
        UpdateCommands();
    }

    public void Dispose()
    {
        TrimPlan.Dispose();
        SplitPlan.Dispose();
    }

    private void Clear()
    {
        SetInPoint(null, null);
        SetOutPoint(null, null);
    }

    /// <summary>Sets the in point, or keeps it and shows <paramref name="rejected"/> text that is not a time.</summary>
    private void SetInPoint(TimeSpan? time, string? rejected)
    {
        _rejectedIn = rejected;
        if (rejected is null) In = time;
        OnPropertyChanged(nameof(InText));
        OnPropertyChanged(nameof(InTextError));
        UpdateCommands();
    }

    private void SetOutPoint(TimeSpan? time, string? rejected)
    {
        _rejectedOut = rejected;
        if (rejected is null) Out = time;
        OnPropertyChanged(nameof(OutText));
        OnPropertyChanged(nameof(OutTextError));
        UpdateCommands();
    }

    private static (TimeSpan? Time, string? Rejected) Parse(string? text) =>
        string.IsNullOrWhiteSpace(text) ? (null, null) : Timecode.TryParse(text, out var time) ? (time, null) : (null, text);

    private void RangeChanged()
    {
        OnPropertyChanged(nameof(HasRange)); OnPropertyChanged(nameof(RangeStart)); OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(RangeError));
        RefreshTrimPlan();
        UpdateCommands();
    }

    private void RefreshTrimPlan()
    {
        if (RangeStart is not { } start || RangeEnd is not { } end) { TrimPlan.Show("Set an in or out point to keep part of this video."); return; }
        if (RangeError is not null) { TrimPlan.Show(""); return; }
        var mode = Mode;
        TrimPlan.Request(async token => PlanDescription.Trim(await _edits.Service.PlanTrimAsync(Source, start, end, mode, token)));
    }

    private void RefreshSplitPlan()
    {
        var position = SplitAt;
        if (!IsInside(position)) { SplitPlan.Show("Move the playhead to where the second part should start."); return; }
        SplitPlan.Request(async token => PlanDescription.Split(await _edits.Service.PlanSplitAsync(Source, position, token)));
    }

    private void Trim()
    {
        // Captured now: the job may start later, and runs on the thread pool.
        var (start, end, title, mode) = (RangeStart!.Value, RangeEnd!.Value, OutputTitle, Mode);
        Job = ClipEditJob.Enqueue(_edits, $"Trim {Source.Label}",
            (editing, progress, token) => editing.TrimAsync(Source, start, end, title, progress, token, mode));
    }

    private void Split()
    {
        var (position, title) = (SplitAt, OutputTitle);
        Job = ClipEditJob.Enqueue(_edits, $"Split {Source.Label}",
            (editing, progress, token) => editing.SplitAsync(Source, position, title, progress, token));
    }

    private bool IsInside(TimeSpan position) => position > TimeSpan.Zero && position < Source.Duration;

    private void UpdateCommands()
    {
        ClearCommand.RaiseCanExecuteChanged();
        TrimCommand.RaiseCanExecuteChanged();
        SplitCommand.RaiseCanExecuteChanged();
    }

    private static string Format(TimeSpan? time) => time is { } value ? Timecode.Format(value) : "";
}
