using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.UI.Controls;

namespace HomeVideoLibrary.Desktop.Views;

public sealed class LibraryNameWindow : Window
{
    public LibraryNameWindow(string currentName, Func<string, Task> saveName)
    {
        Title = "Name your library";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var name = new TextBox { Text = currentName, MaxLength = 120, Watermark = "Library name" };
        AutomationProperties.SetName(name, "Library name");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "error" } };
        var cancel = new PlasticButton { Content = "CANCEL", IsCancel = true, Command = new RelayCommand(Close) };
        var saved = false;
        var save = new AsyncCommand(async _ =>
        {
            error.IsVisible = false;
            name.IsEnabled = cancel.IsEnabled = false;
            try
            {
                await saveName(name.Text!.Trim());
                // The save is durable before the dialog closes.
                saved = true;
                Close();
            }
            finally { name.IsEnabled = cancel.IsEnabled = true; }
        }, () => !string.IsNullOrWhiteSpace(name.Text), exception =>
        {
            error.Text = exception.Message;
            error.IsVisible = true;
        });
        name.TextChanged += (_, _) => save.RaiseCanExecuteChanged();
        Closing += (_, args) => { if (save.IsRunning && !saved) args.Cancel = true; };
        Content = new VcrDialog
        {
            Title = "Name your library",
            Content = new StackPanel { Spacing = 10, Children = { name, error } },
            Footer = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
                Children = { cancel, new PlasticButton { Content = "SAVE", IsDefault = true, Classes = { "primary" }, Command = save } },
            },
        };
        Opened += (_, _) => { name.Focus(); name.SelectAll(); };
    }

}
