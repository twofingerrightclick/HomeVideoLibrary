using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A chapter being edited. <see cref="Start"/> is the model; <see cref="StartText"/> is its edit buffer, so an
/// untouched start keeps the file's full precision.</summary>
public sealed class ChapterEditorViewModel : ViewModelBase
{
    private readonly Action _changed;
    private string _title, _description, _tags, _startText;
    private TimeSpan _start;
    private string? _startError;

    /// <param name="changed">Called after any edit, so the owner can recompute derived state.</param>
    public ChapterEditorViewModel(VideoChapter chapter, Action changed)
    {
        Uid = chapter.Uid;
        _start = chapter.Position;
        _startText = Timecode.Format(chapter.Position);
        _title = chapter.Title;
        _description = chapter.Description;
        _tags = string.Join(Environment.NewLine, chapter.Tags);
        _changed = changed;
    }

    public ulong Uid { get; }
    public TimeSpan Start { get => _start; private set => SetProperty(ref _start, value); }

    /// <summary>Seconds, m:ss or h:mm:ss. Text that does not parse leaves <see cref="Start"/> unchanged and sets
    /// <see cref="StartError"/>.</summary>
    public string StartText
    {
        get => _startText;
        set
        {
            if (!SetProperty(ref _startText, value)) return;
            if (Timecode.TryParse(value, out var start))
            {
                Start = start;
                StartError = null;
            }
            else StartError = $"Chapter \"{Title}\" has an invalid start time \"{value}\". Use seconds, m:ss or h:mm:ss.";
            _changed();
        }
    }

    public string? StartError { get => _startError; private set => SetProperty(ref _startError, value); }
    public string Title { get => _title; set { if (SetProperty(ref _title, value)) _changed(); } }
    public string Description { get => _description; set { if (SetProperty(ref _description, value)) _changed(); } }
    public string Tags { get => _tags; set { if (SetProperty(ref _tags, value)) _changed(); } }

    /// <exception cref="ArgumentException">The start text is invalid; the message names the chapter.</exception>
    public VideoChapter ToChapter() => StartError is { } error
        ? throw new ArgumentException(error)
        : new VideoChapter(Uid, Start, Title, Description, VideoEditorViewModel.ParseTags(Tags));

    /// <summary>True when the edits describe <paramref name="chapter"/> (same tags after parsing, valid start).</summary>
    public bool Matches(VideoChapter chapter) => StartError is null && chapter.Uid == Uid && chapter.Position == Start &&
        chapter.Title == Title && chapter.Description == Description && chapter.Tags.SequenceEqual(VideoEditorViewModel.ParseTags(Tags));

    /// <summary>Shows <paramref name="chapter"/> as the unedited state, touching only fields that differ.</summary>
    public void Reset(VideoChapter chapter)
    {
        if (StartError is not null || Start != chapter.Position)
        {
            _start = chapter.Position;
            _startText = Timecode.Format(chapter.Position);
            _startError = null;
            OnPropertyChanged(nameof(Start)); OnPropertyChanged(nameof(StartText)); OnPropertyChanged(nameof(StartError));
        }
        if (_title != chapter.Title) { _title = chapter.Title; OnPropertyChanged(nameof(Title)); }
        if (_description != chapter.Description) { _description = chapter.Description; OnPropertyChanged(nameof(Description)); }
        var tags = string.Join(Environment.NewLine, chapter.Tags);
        if (_tags != tags) { _tags = tags; OnPropertyChanged(nameof(Tags)); }
    }
}
