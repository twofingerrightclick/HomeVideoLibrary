using Avalonia.Controls;
using Avalonia.Controls.Templates;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

public sealed partial class ChapterDetailsView : UserControl
{
    public ChapterDetailsView() => InitializeComponent();

    /// <summary>Builds a new view for every chapter shown. A XAML DataTemplate would recycle one view, and with it the
    /// description's undo history, which could write one chapter's text into another.</summary>
    public static IDataTemplate ViewTemplate { get; } =
        new FuncDataTemplate<ChapterRowViewModel>((_, _) => new ChapterDetailsView(), supportsRecycling: false);
}
