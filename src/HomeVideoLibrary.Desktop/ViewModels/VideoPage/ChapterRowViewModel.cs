using HomeVideoLibrary.Core;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>One chapter of the open video, and every draft for it. It is keyed by <see cref="Uid"/> and lives as long as
/// the chapter does, so an edit survives playhead updates and reordering. The title has one draft,
/// <see cref="DraftTitle"/>, shared by the expanded editor's title field and the row's inline title field.</summary>
public sealed class ChapterRowViewModel : ViewModelBase
{
    private readonly ChaptersPanelViewModel _panel;
    private TimeSpan _start, _end;
    private string? _draftEnd, _timingError;
    private string _title, _description;
    private string? _draftTitle, _draftStart, _startError;
    private bool _isEditing, _isCurrent;

    internal ChapterRowViewModel(ChaptersPanelViewModel panel, VideoChapter chapter, TagSuggestionProvider suggest)
    {
        _panel = panel;
        Uid = chapter.Uid;
        _start = chapter.Position;
        _end = chapter.End;
        _title = chapter.Title;
        _description = chapter.Description;
        Tags = new TagListViewModel(chapter.Tags, suggest, panel.ChapterEdited);
        ToggleExpandedCommand = new RelayCommand(() => panel.ToggleExpanded(this));
        CollapseCommand = new RelayCommand(() => { if (IsExpanded) panel.ExpandedChapter = null; });
        SeekCommand = new RelayCommand(() => panel.Seek(this));
        CommitTitleCommand = new RelayCommand<string>(title => panel.Rename(this, title));
        CommitDraftTitleCommand = new RelayCommand(() => panel.Rename(this, TitleText));
        CommitDraftEndCommand = new RelayCommand(CommitEnd);
        SetEndTimeCommand = new RelayCommand(() => panel.SetEndAtPlayhead(this), () => panel.CanEndAtPlayhead(this));
        SetStartToPreviousEndCommand = new RelayCommand(() => panel.MoveTo(this, panel.PreviousRangeEnd(this)),
            () => panel.CanMoveTo(this, panel.PreviousRangeEnd(this)));
        SetEndToNextStartCommand = new RelayCommand(() => panel.SetEnd(this, panel.NextRangeStart(this)),
            () => panel.CanSetEnd(this, panel.NextRangeStart(this)));
        CommitDraftStartCommand = new RelayCommand(CommitStart);
        EditCommand = new RelayCommand(() => panel.ExpandedChapter = this, () => panel.CanEdit);
        DeleteCommand = new RelayCommand(() => panel.Delete(this), () => panel.CanEdit);
        MoveToPlayheadCommand = new RelayCommand(() => panel.MoveToPlayhead(this), () => panel.CanMoveToPlayhead(this));
    }

    public ulong Uid { get; }

    public TimeSpan Start
    {
        get => _start;
        internal set { if (SetProperty(ref _start, value)) { OnPropertyChanged(nameof(StartText)); OnPropertyChanged(nameof(StartField)); OnPropertyChanged(nameof(IsMoment)); OnPropertyChanged(nameof(TypeLabel)); } }
    }

    public TimeSpan End
    {
        get => _end;
        internal set { if (SetProperty(ref _end, value)) { OnPropertyChanged(nameof(EndField)); OnPropertyChanged(nameof(EndText)); OnPropertyChanged(nameof(IsMoment)); OnPropertyChanged(nameof(TypeLabel)); } }
    }
    public bool IsMoment => Start == End;
    public string TypeLabel => IsMoment ? "MOMENT" : "CHAPTER";
    public string? TimingError { get => _timingError; internal set => SetProperty(ref _timingError, value); }
    public string EndField { get => _draftEnd ?? Timecode.Format(End); set { if (SetProperty(ref _draftEnd, value)) _panel.DraftChanged(); } }
    public RelayCommand CommitDraftEndCommand { get; }
    public RelayCommand SetEndTimeCommand { get; }
    public RelayCommand SetStartToPreviousEndCommand { get; }
    public RelayCommand SetEndToNextStartCommand { get; }

    public string StartText => Timecode.Format(Start);
    public string EndText => Timecode.Format(End);

    /// <summary>The committed title; empty for an untitled chapter.</summary>
    public string Title
    {
        get => _title;
        internal set { if (SetProperty(ref _title, value)) OnPropertyChanged(nameof(TitleText)); }
    }

    /// <summary>The title being typed, or null when none is. Both title fields write it; Enter or leaving the field
    /// commits it, Escape in the inline field discards it.</summary>
    public string? DraftTitle
    {
        get => _draftTitle;
        set
        {
            if (!SetProperty(ref _draftTitle, value)) return;
            OnPropertyChanged(nameof(TitleText));
            _panel.DraftChanged();
        }
    }

    /// <summary>What a title field shows and edits: the draft while there is one, else the committed title.</summary>
    public string TitleText { get => DraftTitle ?? Title; set => DraftTitle = value; }

    /// <summary>What the start field shows and edits: <c>h:mm:ss.fff</c>, or the text being typed. It becomes
    /// <see cref="Start"/> when committed (Enter or leaving the field) and parses.</summary>
    public string StartField
    {
        get => _draftStart ?? StartText;
        set
        {
            if (!SetProperty(ref _draftStart, value)) return;
            StartError = null;
            _panel.DraftChanged();
        }
    }

    /// <summary>Why the typed start was not accepted; null when it was.</summary>
    public string? StartError { get => _startError; private set => SetProperty(ref _startError, value); }

    /// <summary>Whether something is typed into this chapter but not committed yet.</summary>
    public bool HasOpenEdits => (DraftTitle is { } title && title.Trim() != Title) ||
        (_draftStart is { } start && start.Trim() != StartText) || _draftEnd is not null || Tags.HasOpenDraft;

    /// <summary>Whether the row's inline title field is open.</summary>
    public bool IsEditing { get => _isEditing; set => SetProperty(ref _isEditing, value); }

    /// <summary>Whether the playhead is inside this chapter.</summary>
    public bool IsCurrent { get => _isCurrent; internal set => SetProperty(ref _isCurrent, value); }

    /// <summary>Whether the chapter may be edited: the file's details have been read.</summary>
    public bool CanEdit => _panel.CanEdit;

    /// <summary>Whether this row is expanded into its editor.</summary>
    public bool IsExpanded => _panel.ExpandedChapter == this;

    /// <summary>The expanded editor's content: this row while expanded, else nothing, so the editor's controls exist
    /// only while it shows.</summary>
    public ChapterRowViewModel? Editor => IsExpanded ? this : null;

    public string Description
    {
        get => _description;
        set { if (SetProperty(ref _description, value)) _panel.ChapterEdited(); }
    }

    public TagListViewModel Tags { get; }

    /// <summary>Expands this row, or collapses it when it is the expanded one (the row's click).</summary>
    public RelayCommand ToggleExpandedCommand { get; }
    public RelayCommand CollapseCommand { get; }
    /// <summary>Plays from this chapter's start.</summary>
    public RelayCommand SeekCommand { get; }
    /// <summary>Parameter: the new title (the inline field passes its trimmed draft).</summary>
    public RelayCommand<string> CommitTitleCommand { get; }
    public RelayCommand CommitDraftTitleCommand { get; }
    public RelayCommand CommitDraftStartCommand { get; }
    /// <summary>Expands this row with its title to type.</summary>
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand MoveToPlayheadCommand { get; }

    public VideoChapter ToChapter() => new(Uid, Start, Title, Description, [.. Tags.Items]) { End = End };

    public bool Matches(VideoChapter chapter) => chapter.Uid == Uid && chapter.Position == Start && chapter.End == End && chapter.Title == Title &&
        chapter.Description == Description && Tags.Matches(chapter.Tags);

    /// <summary>Commits valid drafts and retains invalid timing drafts with an actionable error.</summary>
    internal void CommitDrafts()
    {
        if (DraftTitle is { } title) _panel.Rename(this, title);
        IsEditing = false;
        if (_draftStart is not null) CommitStart();
        if (_draftEnd is not null) CommitEnd();

        Tags.CommitDraft();
    }

    /// <summary>Shows <paramref name="chapter"/> (same <see cref="Uid"/>) as the saved state, touching only fields that differ.</summary>
    internal void ApplyBaseline(VideoChapter chapter)
    {
        Start = chapter.Position;
        End = chapter.End;
        _draftEnd = null;
        TimingError = null;
        OnPropertyChanged(nameof(EndField));
        Title = chapter.Title;
        DraftTitle = null;
        IsEditing = false;
        _draftStart = null;
        StartError = null;
        OnPropertyChanged(nameof(StartField));
        Description = chapter.Description;
        Tags.ApplyBaseline(chapter.Tags);
    }

    internal void ExpansionChanged()
    {
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(Editor));
    }

    internal void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        MoveToPlayheadCommand.RaiseCanExecuteChanged();
        SetEndTimeCommand.RaiseCanExecuteChanged();
        SetStartToPreviousEndCommand.RaiseCanExecuteChanged();
        SetEndToNextStartCommand.RaiseCanExecuteChanged();
    }

    private void CommitEnd()
    {
        if (_draftEnd is not { } text) return;
        if (!Timecode.TryParse(text, out var end)) { TimingError = "Use h:mm:ss, m:ss or seconds."; return; }
        _panel.SetEnd(this, end);
        if (TimingError is null) { _draftEnd = null; OnPropertyChanged(nameof(EndField)); }
    }

    private void CommitStart()
    {
        if (_draftStart is not { } text) return;
        if (!Timecode.TryParse(text, out var start) || start >= _panel.Duration)
        {
            StartError = $"Use h:mm:ss, m:ss or seconds, before the end ({Timecode.Format(_panel.Duration)}).";
            return;
        }
        StartError = null;
        if (start != Start) _panel.MoveTo(this, start);
        else TimingError = null;
        if (TimingError is not null) return;
        _draftStart = null;
        OnPropertyChanged(nameof(StartField));
        _panel.DraftChanged();
    }
}
