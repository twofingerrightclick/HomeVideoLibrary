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

    /// <summary>
    /// An expanded chapter row's editor (here just its title), built each time a row expands and never for a
    /// collapsed row, so a field's undo history never reaches another chapter.
    /// </summary>
    public static IDataTemplate RowEditorTemplate { get; } =
        new FuncDataTemplate<ChapterRowViewModel?>((row, _) => row is null ? null : NewTitleField(row), supportsRecycling: false);

    private static TextBox NewTitleField(ChapterRowViewModel row)
    {
        row.DraftTitle ??= row.Title;
        var field = new TextBox
        {
            Margin = new Avalonia.Thickness(12, 2, 10, 10),
            Watermark = "Untitled chapter",
            [!TextBox.TextProperty] = new Avalonia.Data.Binding(nameof(ChapterRowViewModel.DraftTitle)) { Mode = Avalonia.Data.BindingMode.TwoWay },
        };
        field.KeyBindings.Add(new Avalonia.Input.KeyBinding
        {
            Gesture = new Avalonia.Input.KeyGesture(Avalonia.Input.Key.Enter),
            Command = new RelayCommand(() => row.CommitTitle.Execute(row.DraftTitle ?? string.Empty)),
        });
        field.AttachedToVisualTree += (_, _) => field.Focus();
        return field;
    }
}
