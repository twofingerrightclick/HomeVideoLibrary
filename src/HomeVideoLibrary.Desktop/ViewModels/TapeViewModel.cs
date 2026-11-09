using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>One tape on the shelf. Instances are kept per video id and updated in place, so containers, focus and
/// scroll position survive library changes.</summary>
public sealed class TapeViewModel : ViewModelBase
{
    private readonly Action<TapeViewModel, bool> _selectionToggled;
    private readonly Func<TapeViewModel, TapePreviewSession?> _createPreview;
    private VideoRecord _record;
    private LibraryVideo _video;
    private bool _isSelected;
    private VideoChapterRecord? _matchedChapter;

    /// <param name="selectionToggled">Called when the user changes <see cref="IsSelected"/> (for example, its checkbox).</param>
    /// <param name="createPreview">Starts a hover preview session, or returns null when previews are unavailable.</param>
    public TapeViewModel(VideoRecord record, Action<TapeViewModel, bool> selectionToggled,
        Func<TapeViewModel, TapePreviewSession?> createPreview, bool isSelected = false)
    {
        _record = record; _video = record.ToLibraryVideo(); _selectionToggled = selectionToggled; _createPreview = createPreview;
        _isSelected = isSelected;
    }

    /// <summary>A hover preview for this tape's current record; the caller disposes it. Null when previews are off.</summary>
    public TapePreviewSession? CreatePreviewSession() => _createPreview(this);

    public Guid Id => _record.Id;
    public VideoRecord Record => _record;
    public LibraryVideo Video => _video;
    public string Label => _record.Label.Replace('\n', ' ').Replace('\r', ' ');
    public string DateLabel => _record.RecordedAt.ToLocalTime().ToString("MMM yyyy");
    public string MonthLabel => _record.RecordedAt.ToLocalTime().ToString("MMMM yyyy");
    public string DurationLabel => _record.Duration.TotalHours >= 1 ? _record.Duration.ToString(@"h\:mm\:ss") : _record.Duration.ToString(@"m\:ss");
    public string Accent => new[] { "#947055", "#526F7B", "#7A5559", "#607563" }[_record.Id.ToByteArray()[0] % 4];
    public bool IsMissing => _record.State == VideoRecordState.Missing;
    public string StateLabel => _record.State switch
    {
        VideoRecordState.Missing => "File missing",
        VideoRecordState.Unreadable => "Unreadable",
        _ => "",
    };
    public bool HasStateLabel => StateLabel.Length > 0;
    public string? StateDetail => _record.Error;

    /// <summary>The first chapter matching the current search, if any.</summary>
    public VideoChapterRecord? MatchedChapter { get => _matchedChapter; internal set => SetProperty(ref _matchedChapter, value); }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetProperty(ref _isSelected, value)) _selectionToggled(this, value); }
    }

    internal void SetSelected(bool value) => SetProperty(ref _isSelected, value, nameof(IsSelected));

    internal void Update(VideoRecord record)
    {
        if (ReferenceEquals(record, _record)) return;
        _record = record; _video = record.ToLibraryVideo();
        foreach (var name in new[] { nameof(Record), nameof(Video), nameof(Label), nameof(DateLabel), nameof(MonthLabel), nameof(DurationLabel),
                     nameof(IsMissing), nameof(StateLabel), nameof(HasStateLabel), nameof(StateDetail) })
            OnPropertyChanged(name);
    }
}

/// <summary>A shelf row of one or two tapes, keyed by its left tape.</summary>
public sealed class TapeRow(TapeViewModel left) : ViewModelBase
{
    private TapeViewModel? _right;
    public TapeViewModel Left { get; } = left;
    public TapeViewModel? Right { get => _right; internal set { if (SetProperty(ref _right, value)) OnPropertyChanged(nameof(HasRight)); } }
    public bool HasRight => Right is not null;
}
