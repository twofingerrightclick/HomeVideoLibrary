using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

public sealed partial class ChaptersTab : UserControl
{
    public ChaptersTab() => InitializeComponent();

    private void OnChapterEntered(object? sender, PointerEventArgs args)
    {
        if (DataContext is VideoPageViewModel page && sender is Control { DataContext: ChapterRowViewModel row })
            page.Details.Chapters.HoveredChapter = row;
    }

    private void OnChapterExited(object? sender, PointerEventArgs args)
    {
        if (DataContext is VideoPageViewModel page && sender is Control control &&
            ReferenceEquals(page.Details.Chapters.HoveredChapter, control.DataContext))
            page.Details.Chapters.HoveredChapter = null;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (DataContext is VideoPageViewModel page) page.Details.Chapters.HoveredChapter = null;
    }
}
