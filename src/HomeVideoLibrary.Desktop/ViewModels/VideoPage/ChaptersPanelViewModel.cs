using System.Security.Cryptography;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>A tag used by chapters, and those chapters in start order.</summary>
public sealed record ChapterTagGroup(string Tag, IReadOnlyList<ChapterRowViewModel> Chapters);

/// <summary>The chapters of the open video. <see cref="Rows"/> holds one <see cref="ChapterRowViewModel"/> per chapter in
/// start order plus the single <see cref="PlaceholderRowViewModel"/>, and is patched in place: a playhead update only
/// changes <see cref="ChapterRowViewModel.IsCurrent"/> and moves the placeholder, so rows (and an edit in progress) are
/// never rebuilt. <see cref="Marks"/> is the immutable snapshot the scrub bar draws, republished when chapters change.
/// Selection (<see cref="SelectedChapter"/>) is explicit and never follows the playhead, so typing in the details pane
/// cannot jump to another chapter while the video plays.</summary>
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
    private ChapterRowViewModel? _selectedChapter;
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
        AddChapterCommand = new RelayCommand(AddAtPlayhead, () => CanAddChapter);
        RenameCurrentCommand = new RelayCommand(RenameCurrent, () => CanEdit && CurrentChapter is not null);
        PreviousChapterCommand = new RelayCommand(() => _seek(PreviousStart()));
        NextChapterCommand = new RelayCommand(() => { if (NextStart() is { } next) _seek(next); });
        PlacePlayhead();
        Publish();
    }

    public PatchableCollection<ViewModelBase> Rows { get; } = [];
    public PlaceholderRowViewModel Placeholder => _placeholder;
    public IReadOnlyList<ChapterRowViewModel> Chapters => _chapters;
    public IReadOnlyList<TimeSpan> Starts => _chapters.Select(chapter => chapter.Start).ToArray();
    public IReadOnlyList<IChapterMark> Marks { get => _marks; private set => SetProperty(ref _marks, value); }

    /// <summary>Chapter tags, most used first, each with the chapters using it.</summary>
    public IReadOnlyList<ChapterTagGroup> TagGroups { get => _tagGroups; private set => SetProperty(ref _tagGroups, value); }

    /// <summary>The chapter whose details are shown. Set by jumping to a row or adding one; the playhead never changes it.</summary>
    public ChapterRowViewModel? SelectedChapter
    {
        get => _selectedChapter;
        set
        {
            var previous = _selectedChapter;
            if (!SetProperty(ref _selectedChapter, value)) return;
            if (previous is not null) previous.IsSelected = false;
            if (value is not null) value.IsSelected = true;
        }
    }

    public ChapterRowViewModel? CurrentChapter => _chapters.FirstOrDefault(chapter => chapter.IsCurrent);

    /// <summary>Whether edits are allowed: the file's details have been read and no save is replacing them.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set
        {
            if (!SetProperty(ref _canEdit, value)) return;
            PlacePlayhead();
            foreach (var chapter in _chapters) chapter.UpdateCommands();
        }
    }

    /// <summary>Whether a chapter can be added at the playhead: none starts within <see cref="SameChapterTolerance"/>.
    /// Holding the add key therefore adds one chapter, not one per key repeat.</summary>
    public bool CanAddChapter => CanEdit && !ChapterPlacement.HasChapterAt(Starts, _playhead, SameChapterTolerance);

    public RelayCommand AddChapterCommand { get; }
    public RelayCommand RenameCurrentCommand { get; }
    /// <summary>Seeks to the current chapter's start, or to the previous chapter's when just past the current start.</summary>
    public RelayCommand PreviousChapterCommand { get; }
    public RelayCommand NextChapterCommand { get; }

    /// <summary>Follows playback: marks the current chapter and moves the placeholder.</summary>
    public void SetPlayhead(TimeSpan playhead)
    {
        if (_playhead == playhead) return;
        _playhead = playhead;
        PlacePlayhead();
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
        if (SelectedChapter is { } selected && !uids.Contains(selected.Uid)) SelectedChapter = null;
        foreach (var chapter in chapters)
        {
            if (_chapters.FirstOrDefault(row => row.Uid == chapter.Uid) is { } row) row.ApplyBaseline(chapter);
            else _chapters.Add(new ChapterRowViewModel(this, chapter, _suggest));
        }
        Sort();
        PlacePlayhead();
        Publish();
    }

    internal void Jump(ChapterRowViewModel row)
    {
        SelectedChapter = row;
        _seek(row.Start);
    }

    internal void Rename(ChapterRowViewModel row, string title)
    {
        row.Title = title.Trim();
        Publish();
        _changed();
    }

    internal void Delete(ChapterRowViewModel row)
    {
        if (!CanEdit || !_chapters.Remove(row)) return;
        if (SelectedChapter == row) SelectedChapter = null;
        PlacePlayhead();
        Publish();
        _changed();
    }

    internal void MoveToPlayhead(ChapterRowViewModel row)
    {
        if (!CanEdit) return;
        row.Start = _playhead;
        Sort();
        PlacePlayhead();
        Publish();
        _changed();
    }

    internal void ChapterEdited(ChapterRowViewModel row)
    {
        PublishTagGroups();
        _changed();
    }

    private void AddAtPlayhead()
    {
        if (!CanAddChapter) return;
        var row = new ChapterRowViewModel(this, new VideoChapter(NewUid(), _playhead, "", "", []), _suggest) { IsEditing = true };
        _chapters.Add(row);
        Sort();
        SelectedChapter = row;
        PlacePlayhead();
        Publish();
        _changed();
    }

    private void RenameCurrent()
    {
        if (CurrentChapter is not { } current) return;
        SelectedChapter = current;
        current.IsEditing = true;
    }

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

    /// <summary>Derives everything that depends on the playhead, then converges <see cref="Rows"/> on the chapters in
    /// order with the placeholder at its index, which for a playhead update is at most one Move.</summary>
    private void PlacePlayhead()
    {
        var starts = Starts;
        for (var index = 0; index < _chapters.Count; index++)
        {
            var end = index + 1 < _chapters.Count ? _chapters[index + 1].Start : _duration;
            _chapters[index].IsCurrent = _playhead >= _chapters[index].Start && _playhead < end;
        }
        _placeholder.Start = _playhead;
        _placeholder.IsShown = CanAddChapter;
        var desired = new List<ViewModelBase>(_chapters);
        desired.Insert(ChapterPlacement.PlaceholderIndex(starts, _playhead), _placeholder);
        Rows.Patch(desired);
        OnPropertyChanged(nameof(CanAddChapter));
        OnPropertyChanged(nameof(CurrentChapter));
        AddChapterCommand.RaiseCanExecuteChanged();
        RenameCurrentCommand.RaiseCanExecuteChanged();
    }

    private void Publish()
    {
        Marks = _chapters.Select(chapter => (IChapterMark)new ChapterMark(chapter.Start, chapter.Title)).ToArray();
        PublishTagGroups();
    }

    private void PublishTagGroups() => TagGroups = _chapters
        .SelectMany(chapter => chapter.Tags.Items.Select(tag => (Tag: tag, Chapter: chapter)))
        .GroupBy(pair => pair.Tag, TagRules.Comparer)
        .Select(group => new ChapterTagGroup(group.First().Tag, group.Select(pair => pair.Chapter).Distinct().ToArray()))
        .OrderByDescending(group => group.Chapters.Count).ThenBy(group => group.Tag, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();
}
