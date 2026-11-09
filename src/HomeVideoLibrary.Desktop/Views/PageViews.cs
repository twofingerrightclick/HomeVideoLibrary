using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Desktop.Views.VideoPage;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>Builds a new view for every page. A XAML DataTemplate as a ContentTemplate would recycle one view across
/// pages, and with it TextBox undo history, which can write one video's text into another.</summary>
public static class PageViews
{
    public static IDataTemplate Template { get; } = new FuncDataTemplate<PageViewModel?>((page, _) => page switch
    {
        null => null,
        VideoPageViewModel => new VideoPageView(),
        ClipEditorPage clips => new ClipEditorView { DataContext = clips.Editor },
        _ => throw new ArgumentOutOfRangeException(nameof(page), page.GetType().Name, "No view for this page."),
    }, supportsRecycling: false);
}
