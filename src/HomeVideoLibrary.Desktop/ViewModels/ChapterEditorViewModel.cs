using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class ChapterEditorViewModel(VideoChapter chapter) : ViewModelBase
{
    private string _title = chapter.Title;
    private string _description = chapter.Description;
    private string _tags = string.Join(Environment.NewLine, chapter.Tags);
    private string _position = chapter.Position.ToString("c");
    public ulong Uid { get; } = chapter.Uid;
    public string Position { get => _position; set => SetProperty(ref _position, value); }
    public string Title { get => _title; set => SetProperty(ref _title, value); }
    public string Description { get => _description; set => SetProperty(ref _description, value); }
    public string Tags { get => _tags; set => SetProperty(ref _tags, value); }
    public VideoChapter ToChapter() => new(Uid, TimeSpan.TryParse(Position, System.Globalization.CultureInfo.InvariantCulture, out var position)
        ? position : throw new ArgumentException("Chapter position must use hours:minutes:seconds."), Title, Description, VideoEditorViewModel.ParseTags(Tags));
}
