using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Demo;

/// <summary>
/// Everything editable about one open video. A new instance is created for each video, so drafts and the
/// editing row belong to that video and vanish with it.
/// </summary>
public sealed class VideoPageViewModel(string name, ChaptersPanelViewModel chapters, TagEditorViewModel tags)
{
    public string Name { get; } = name;

    public ChaptersPanelViewModel Chapters { get; } = chapters;

    public TagEditorViewModel Tags { get; } = tags;

    /// <summary>
    /// Builds a new view for every page. A XAML DataTemplate would recycle one view across videos, and
    /// with it TextBox undo history, which can write one video's text into another.
    /// </summary>
    public static IDataTemplate ViewTemplate { get; } =
        new FuncDataTemplate<VideoPageViewModel>((_, _) => new VideoPageView(), supportsRecycling: false);
}
