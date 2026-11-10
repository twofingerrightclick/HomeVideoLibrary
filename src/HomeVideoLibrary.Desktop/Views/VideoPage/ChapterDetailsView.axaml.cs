using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.UI.Controls;

namespace HomeVideoLibrary.Desktop.Views.VideoPage;

/// <summary>An expanded chapter row's editor.</summary>
public sealed partial class ChapterDetailsView : UserControl
{
    public ChapterDetailsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, CollapseOnEscape, RoutingStrategies.Bubble);
    }

    /// <summary>Builds a new editor every time a row expands, and none for a collapsed row. A XAML DataTemplate would
    /// recycle one editor, and with it its fields' undo history, which could write one chapter's text into another.</summary>
    public static IDataTemplate ViewTemplate { get; } =
        new FuncDataTemplate<ChapterRowViewModel?>((row, _) => row is null ? null : new ChapterDetailsView(), supportsRecycling: false);

    // Escape collapses the row (keeping what was typed) and leaves focus on the row, which outlives this editor.
    private void CollapseOnEscape(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || args.Handled || DataContext is not ChapterRowViewModel row) return;
        args.Handled = true;
        this.FindAncestorOfType<ChapterListItem>()?.Focus(NavigationMethod.Tab);
        row.CollapseCommand.Execute(null);
    }
}
