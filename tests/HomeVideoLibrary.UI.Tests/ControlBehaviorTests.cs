using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Controls;
using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

/// <summary>Interaction contracts the app relies on, driven through headless input.</summary>
public sealed class ControlBehaviorTests
{
    private static readonly TimeSpan Length = TimeSpan.FromMinutes(40);

    [AvaloniaFact]
    public void Chapter_row_enters_edit_mode_when_a_placeholder_is_promoted_after_IsEditing_was_set()
    {
        var row = new ChapterListItem { IsPlaceholder = true, Start = TimeSpan.FromMinutes(3) };
        Host(row);

        row.IsEditing = true;
        Assert.DoesNotContain(":editing", row.Classes);

        row.IsPlaceholder = false;

        Assert.Contains(":editing", row.Classes);
    }

    private static (Window Window, T Control) Host<T>(T control)
        where T : Control
    {
        var window = new Window { Width = 800, Height = 200, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, control);
    }

    private static Point At(Control control, double fraction) =>
        control.TranslatePoint(new Point(control.Bounds.Width * fraction, control.Bounds.Height / 2), TopLevel.GetTopLevel(control)!)!.Value;
}
