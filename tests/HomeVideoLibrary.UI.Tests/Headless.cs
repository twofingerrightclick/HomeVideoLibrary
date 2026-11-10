using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace HomeVideoLibrary.UI.Tests;

internal static class Headless
{
    public static (Window Window, T Control) Host<T>(T control)
        where T : Control
    {
        var window = new Window { Width = 800, Height = 300, Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, control);
    }

    public static Point At(Control control, double fraction) =>
        control.TranslatePoint(new Point(control.Bounds.Width * fraction, control.Bounds.Height / 2), TopLevel.GetTopLevel(control)!)!.Value;
}

/// <summary>Records every execution, standing in for a view-model command.</summary>
internal sealed class RecordingCommand : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public List<object?> Calls { get; } = [];

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => Calls.Add(parameter);
}
