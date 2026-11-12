namespace HomeVideoLibrary.UI.Logic;

public interface IChapterMark
{
    TimeSpan Start { get; }
    TimeSpan End => Start;

    string Title { get; }
}

public sealed record ChapterMark(TimeSpan Start, string Title) : IChapterMark
{
    public TimeSpan End { get; init; } = Start;
}
