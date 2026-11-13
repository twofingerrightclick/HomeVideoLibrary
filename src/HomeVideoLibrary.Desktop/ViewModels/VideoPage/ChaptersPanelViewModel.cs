using System.Collections.ObjectModel;
using System.Security.Cryptography;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>A tag used by chapters, and those chapters in start order.</summary>
public sealed record ChapterTagGroup(string Tag, IReadOnlyList<ChapterRowViewModel> Chapters);

/// <summary>The chapters of the open video. <see cref="Rows"/> holds one <see cref="ChapterRowViewModel"/> per chapter in
/// start order plus the single <see cref="PlaceholderRowViewModel"/>. A playhead update changes only
/// <see cref="ChapterRowViewModel.IsCurrent"/> and moves the placeholder itself, so no chapter row's container is
/// rebuilt while it is clicked, double-clicked or edited. <see cref="Marks"/> is the immutable snapshot the scrub bar
/// draws, republished when chapters change. One row at a time is expanded into its editor
/// (<see cref="ExpandedChapter"/>); that is explicit and never follows the playhead, so typing cannot jump to another
/// chapter while the video plays.</summary>
public sealed class ChaptersPanelViewModel : ViewModelBase
{
    /// <summary>A chapter closer than this to the playhead counts as "at" it: no new one is added there.</summary>
    public static readonly TimeSpan SameChapterTolerance = TimeSpan.FromSeconds(1);
    // Within this of a chapter's start, "previous chapter" goes to the chapter before it instead of restarting it.
    private static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(2);

    private readonly List<ChapterRowViewModel> _chapters = [];
    private readonly PlaceholderRowViewModel _placeholder = new();
    private readonly TimeSpan _duration;
    private readonly TagSuggestionProvider _suggest;
    private readonly Action<TimeSpan> _seek;
    private readonly Action _changed;
    private TimeSpan _playhead;
    private bool _canEdit;
    private ChapterRowViewModel? _expandedChapter;
    private IReadOnlyList<IChapterMark> _marks = [];
    private IReadOnlyList<ChapterTagGroup> _tagGroups = [];

    /// <param name="seek">Moves playback (a row's jump).</param>
    /// <param name="changed">Called after any chapter edit, so the owner can recompute derived state.</param>
    public ChaptersPanelViewModel(TimeSpan duration, IReadOnlyList<VideoChapter> chapters, TagSuggestionProvider suggest,
        Action<TimeSpan> seek, Action changed)
    {
        _duration = duration; _suggest = suggest; _seek = seek; _changed = changed;
        foreach (var chapter in chapters) _chapters.Add(new ChapterRowViewModel(this, chapter, suggest));
        Sort();
        AddChapterCommand = new RelayCommand(() => AddAtPlayhead(expand: true), () => CanAddChapter);
        AddMomentCommand = new RelayCommand(() => AddAtPlayhead(true, true), () => CanAddMoment);
        RenameCurrentCommand = new RelayCommand(() => ExpandedChapter = CurrentChapter, () => CanEdit && CurrentChapter is not null);
        PreviousChapterCommand = new RelayCommand(() => _seek(PreviousStart()));
        NextChapterCommand = new RelayCommand(() => { if (NextStart() is { } next) _seek(next); });
        SyncRows();
        Publish();
    }

    public ObservableCollection<ViewModelBase> Rows { get; } = [];
    public PlaceholderRowViewModel Placeholder => _placeholder;
    public IReadOnlyList<ChapterRowViewModel> Chapters => _chapters;
    public bool HasChapters => _chapters.Count > 0;
    public IReadOnlyList<TimeSpan> Starts => _chapters.Select(chapter => chapter.Start).ToArray();
    public IReadOnlyList<IChapterMark> Marks { get => _marks; private set => SetProperty(ref _marks, value); }

    /// <summary>Chapter tags, most used first, each with the chapters using it.</summary>
    public IReadOnlyList<ChapterTagGroup> TagGroups { get => _tagGroups; private set => SetProperty(ref _tagGroups, value); }

    /// <summary>The chapter shown expanded into its editor, or null. Collapsing a row commits what was typed in it, as
    /// leaving its fields would.</summary>
    public ChapterRowViewModel? ExpandedChapter
    {
        get => _expandedChapter;
        set
        {
            var previous = _expandedChapter;
            if (!SetProperty(ref _expandedChapter, value)) return;
            if (previous is not null)
            {
                previous.CommitDrafts();
                previous.ExpansionChanged();
            }
            value?.ExpansionChanged();
        }
    }

    public ChapterRowViewModel? CurrentChapter => _chapters.FirstOrDefault(chapter => chapter.IsCurrent);

    /// <summary>Whether edits are allowed: the file's details have been read.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set
        {
            if (!SetProperty(ref _canEdit, value)) return;
            FollowPlayhead();
            foreach (var chapter in _chapters) chapter.UpdateCommands();
        }
    }

    public TimeSpan Duration => _duration;

    /// <summary>Whether a range can be added before the clip end without repeating another range start.
    /// Holding the add key therefore adds one chapter, not one per key repeat.</summary>
    public bool CanAddChapter => CanAdd(false);
    public bool CanAddMoment => CanAdd(true);
    private bool CanAdd(bool moment) => CanEdit && _playhead >= TimeSpan.Zero && _playhead < _duration &&
        !_chapters.Any(c => c.IsMoment == moment && c.Start == _playhead);
    public string? TimingConflict => ChapterTiming.Conflict(ToChapters(), _duration);
    public RelayCommand AddMomentCommand { get; }

    /// <summary>Whether something is typed into a chapter but not committed yet.</summary>
    public bool HasOpenEdits => _chapters.Any(chapter => chapter.HasOpenEdits);

    /// <summary>Adds a chapter at the playhead, expanded for typing its title.</summary>
    public RelayCommand AddChapterCommand { get; }
    /// <summary>Expands the chapter under the playhead.</summary>
    public RelayCommand RenameCurrentCommand { get; }
    /// <summary>Seeks to the current chapter's start, or to the previous chapter's when just past the current start.</summary>
    public RelayCommand PreviousChapterCommand { get; }
    public RelayCommand NextChapterCommand { get; }

    /// <summary>Follows playback: marks the current chapter and moves the placeholder.</summary>
    public void SetPlayhead(TimeSpan playhead)
    {
        if (_playhead == playhead) return;
        _playhead = playhead;
        FollowPlayhead();
    }

    /// <summary>Adds a chapter at the playhead; <paramref name="expand"/> opens its editor with the title to type.
    /// Returns null when a chapter already starts there.</summary>
    public ChapterRowViewModel? AddAtPlayhead(bool expand, bool moment = false)
    {
        if (!CanAdd(moment)) return null;
        var proposed = new VideoChapter(NewUid(), _playhead, "", "", [])
        { End = moment ? _playhead : TimeSpan.FromTicks(Math.Min(_duration.Ticks, (_playhead + TimeSpan.FromMinutes(10)).Ticks)) };
        var updated = ChapterTiming.Apply(ToChapters(), proposed, _duration);
        foreach (var existing in _chapters) existing.End = updated.Single(c => c.Uid == existing.Uid).End;
        var row = new ChapterRowViewModel(this, updated.Single(c => c.Uid == proposed.Uid), _suggest);
        _chapters.Add(row);
        Sort();
        SyncRows();
        if (expand) ExpandedChapter = row;
        Publish();
        _changed();
        return row;
    }

    /// <summary>Commits everything typed into chapters (titles, start times, tags), as Enter or leaving the fields would.</summary>
    public void CommitOpenEdits()
    {
        foreach (var chapter in _chapters.ToArray()) chapter.CommitDrafts();
    }

    public IReadOnlyList<VideoChapter> ToChapters() => _chapters.Select(chapter => chapter.ToChapter()).ToArray();

    /// <summary>Whether the rows describe exactly <paramref name="chapters"/>, matched by UID.</summary>
    public bool Matches(IReadOnlyList<VideoChapter> chapters) => chapters.Count == _chapters.Count &&
        chapters.All(chapter => _chapters.FirstOrDefault(row => row.Uid == chapter.Uid) is { } row && row.Matches(chapter));

    /// <summary>Shows <paramref name="chapters"/> as the saved state. Rows are matched by UID and keep their instances;
    /// chapters that are gone are removed and new ones inserted in start order.</summary>
    public void ApplyBaseline(IReadOnlyList<VideoChapter> chapters)
    {
        var uids = chapters.Select(chapter => chapter.Uid).ToHashSet();
        _chapters.RemoveAll(row => !uids.Contains(row.Uid));
        if (ExpandedChapter is { } expanded && !uids.Contains(expanded.Uid)) ExpandedChapter = null;
        foreach (var chapter in chapters)
        {
            if (_chapters.FirstOrDefault(row => row.Uid == chapter.Uid) is { } row) row.ApplyBaseline(chapter);
            else _chapters.Add(new ChapterRowViewModel(this, chapter, _suggest));
        }
        Sort();
        SyncRows();
        Publish();
    }

    internal void Seek(ChapterRowViewModel row) { ExpandedChapter = row; _seek(row.Start); }

    internal void ToggleExpanded(ChapterRowViewModel row) => ExpandedChapter = ExpandedChapter == row ? null : row;

    internal void Rename(ChapterRowViewModel row, string title)
    {
        row.DraftTitle = null;
        var trimmed = title.Trim();
        if (row.Title == trimmed) return;
        row.Title = trimmed;
        Publish();
        _changed();
    }

    internal void Delete(ChapterRowViewModel row)
    {
        if (!CanEdit || !_chapters.Contains(row)) return;
        if (ExpandedChapter == row) ExpandedChapter = null;
        _chapters.Remove(row);
        SyncRows();
        Publish();
        _changed();
    }

    internal void MoveToPlayhead(ChapterRowViewModel row) => MoveTo(row, _playhead);

    internal TimeSpan PreviousRangeEnd(ChapterRowViewModel row) => _chapters
        .Where(c => c.Uid != row.Uid && !c.IsMoment && c.Start < row.Start)
        .OrderBy(c => c.Start).LastOrDefault()?.End ?? TimeSpan.Zero;

    internal TimeSpan NextRangeStart(ChapterRowViewModel row) => _chapters
        .Where(c => c.Uid != row.Uid && !c.IsMoment && c.Start > row.Start)
        .OrderBy(c => c.Start).FirstOrDefault()?.Start ?? _duration;

    internal bool CanMoveToPlayhead(ChapterRowViewModel row) => CanMoveTo(row, _playhead);
    internal bool CanEndAtPlayhead(ChapterRowViewModel row) => CanSetEnd(row, _playhead);

    internal bool CanMoveTo(ChapterRowViewModel row, TimeSpan start) =>
        (row.IsMoment || start < row.End) && CanApplyTiming(row.ToChapter() with
        { Position = start, End = row.IsMoment ? start : row.End });

    internal bool CanSetEnd(ChapterRowViewModel row, TimeSpan end) => CanApplyTiming(row.ToChapter() with { End = end });

    private bool CanApplyTiming(VideoChapter proposed)
    {
        if (!CanEdit) return false;
        try { ChapterTiming.Apply(ToChapters(), proposed, _duration); return true; }
        catch (ArgumentException) { return false; }
    }

    internal void MoveTo(ChapterRowViewModel row, TimeSpan start)
    {
        if (!CanEdit) return;
        if (!row.IsMoment && start >= row.End) { row.TimingError = "Start must precede this chapter’s end."; return; }
        ApplyTiming(row, row.ToChapter() with { Position = start, End = row.IsMoment ? start : row.End });
    }

    internal void SetEnd(ChapterRowViewModel row, TimeSpan end) => ApplyTiming(row, row.ToChapter() with { End = end });
    internal void SetEndAtPlayhead(ChapterRowViewModel row) => SetEnd(row, _playhead);

    private void ApplyTiming(ChapterRowViewModel row, VideoChapter proposed)
    {
        if (!CanEdit) return;
        IReadOnlyList<VideoChapter> updated;
        try { updated = ChapterTiming.Apply(ToChapters(), proposed, _duration); }
        catch (ArgumentException exception) { row.TimingError = exception.Message; return; }
        row.TimingError = null;
        foreach (var existing in _chapters)
        {
            var value = updated.Single(c => c.Uid == existing.Uid);
            existing.Start = value.Position;
            existing.End = value.End;
        }
        Sort();
        SyncRows();
        Publish();
        _changed();
    }

    internal void ChapterEdited()
    {
        PublishTagGroups();
        _changed();
    }

    internal void DraftChanged() => _changed();

    private TimeSpan PreviousStart()
    {
        var current = _chapters.LastOrDefault(chapter => chapter.Start <= _playhead)?.Start ?? TimeSpan.Zero;
        if (_playhead - current >= RestartWindow) return current;
        return _chapters.LastOrDefault(chapter => chapter.Start < current)?.Start ?? TimeSpan.Zero;
    }

    private TimeSpan? NextStart() => _chapters.FirstOrDefault(chapter => chapter.Start > _playhead + TimeSpan.FromMilliseconds(500))?.Start;

    private ulong NewUid()
    {
        Span<byte> bytes = stackalloc byte[8];
        ulong uid;
        do { RandomNumberGenerator.Fill(bytes); uid = BitConverter.ToUInt64(bytes); }
        while (uid == 0 || _chapters.Any(chapter => chapter.Uid == uid));
        return uid;
    }

    private void Sort() => _chapters.Sort((left, right) =>
        left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Uid.CompareTo(right.Uid));

    /// <summary>Converges <see cref="Rows"/> on the chapters after they were added, removed or reordered. The placeholder
    /// is parked at the end meanwhile, so only chapter rows that actually changed place move.</summary>
    private void SyncRows()
    {
        var placeholder = Rows.IndexOf(_placeholder);
        if (placeholder < 0) Rows.Add(_placeholder);
        else if (placeholder != Rows.Count - 1) Rows.Move(placeholder, Rows.Count - 1);
        for (var index = Rows.Count - 2; index >= 0; index--)
            if (!_chapters.Contains((ChapterRowViewModel)Rows[index])) Rows.RemoveAt(index);
        for (var index = 0; index < _chapters.Count; index++)
        {
            var current = Rows.IndexOf(_chapters[index]);
            if (current < 0) Rows.Insert(index, _chapters[index]);
            else if (current != index) Rows.Move(current, index);
        }
        FollowPlayhead();
    }

    /// <summary>Derives everything that depends on the playhead. Moves only the placeholder, at most once.</summary>
    private void FollowPlayhead()
    {
        var starts = Starts;
        for (var index = 0; index < _chapters.Count; index++)
        {
            var end = _chapters[index].End;
            _chapters[index].IsCurrent = _playhead >= _chapters[index].Start && _playhead < end;
            _chapters[index].UpdateCommands();
        }
        _placeholder.Start = _playhead;
        _placeholder.IsShown = CanAddChapter;
        var target = ChapterPlacement.PlaceholderIndex(starts, _playhead);
        var current = Rows.IndexOf(_placeholder);
        if (current >= 0 && current != target) Rows.Move(current, target);
        OnPropertyChanged(nameof(CanAddChapter));
        OnPropertyChanged(nameof(CanAddMoment));
        AddMomentCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentChapter));
        AddChapterCommand.RaiseCanExecuteChanged();
        RenameCurrentCommand.RaiseCanExecuteChanged();
    }

    private void Publish()
    {
        Marks = _chapters.Select(chapter => (IChapterMark)new ChapterMark(chapter.Start, chapter.Title) { End = chapter.End }).ToArray();
        FollowPlayhead();
        OnPropertyChanged(nameof(TimingConflict));
        OnPropertyChanged(nameof(HasChapters));
        PublishTagGroups();
    }

    private void PublishTagGroups() => TagGroups = _chapters
        .SelectMany(chapter => chapter.Tags.Items.Select(tag => (Tag: tag, Chapter: chapter)))
        .GroupBy(pair => pair.Tag, TagRules.Comparer)
        .Select(group => new ChapterTagGroup(group.First().Tag, group.Select(pair => pair.Chapter).Distinct().ToArray()))
        .OrderByDescending(group => group.Chapters.Count).ThenBy(group => group.Tag, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
}
