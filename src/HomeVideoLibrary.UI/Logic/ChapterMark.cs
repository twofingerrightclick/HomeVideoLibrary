namespace HomeVideoLibrary.UI.Logic;

public interface IChapterMark
{
    TimeSpan Start { get; }

    string Title { get; }
}

public sealed record ChapterMark(TimeSpan Start, string Title) : IChapterMark;
