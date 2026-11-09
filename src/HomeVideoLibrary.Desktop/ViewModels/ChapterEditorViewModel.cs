using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class ChapterEditorViewModel(VideoChapter chapter) : ViewModelBase
{
    private readonly string _loadedPosition = Timecode.Format(chapter.Position);
    private string _title = chapter.Title;
    private string _description = chapter.Description;
    private string _tags = string.Join(Environment.NewLine, chapter.Tags);
    private string _position = Timecode.Format(chapter.Position);
    public ulong Uid { get; } = chapter.Uid;
    public string Position { get => _position; set => SetProperty(ref _position, value); }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }

    /// <exception cref="ArgumentException">The start time text is not a timecode; the message names the chapter.</exception>
    public TimeSpan ParsePosition()
    {
        // The text shows milliseconds; an untouched field keeps the file's full precision.
        if (Position == _loadedPosition) return chapter.Position;
        return Timecode.TryParse(Position, out var position) ? position
            : throw new ArgumentException($"Chapter \"{Title}\" has an invalid start time \"{Position}\". Use seconds, m:ss or h:mm:ss.");
    }

    public VideoChapter ToChapter() => new(Uid, ParsePosition(), Title, Description, VideoEditorViewModel.ParseTags(Tags));
}
