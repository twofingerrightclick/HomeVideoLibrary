using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.UI.Controls;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>A modal question as a real window, so it shows above the native video surface. Each key closes the window
/// with its answer; closing it any other way answers <c>dismissed</c>.</summary>
public sealed class PromptWindow<TAnswer> : Window
{
    public sealed record Option(string Label, TAnswer Answer, string? Classes = null, bool IsDefault = false, bool IsCancel = false);

    public PromptWindow(string title, string message, TAnswer dismissed, IReadOnlyList<Option> keys)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        // The VcrDialog draws the title; a native title bar would repeat it.
        SystemDecorations = SystemDecorations.BorderOnly;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 380;
        MaxWidth = 560;
        Dismissed = dismissed;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var key in keys)
        {
            var button = new PlasticButton
            {
                Content = key.Label, IsDefault = key.IsDefault, IsCancel = key.IsCancel,
                Command = new RelayCommand(() => Close(key.Answer)),
            };
            if (key.Classes is { } classes) button.Classes.Add(classes);
            footer.Children.Add(button);
        }
        Content = new VcrDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 500 },
            Footer = footer,
        };
        Opened += (_, _) => (keys.Count > 0 ? footer.Children.OfType<Button>().FirstOrDefault(b => b.IsDefault) ?? footer.Children[0] : this)
            .Focus(NavigationMethod.Tab);
    }

    public TAnswer Dismissed { get; }

    /// <summary>Shows the prompt modally over <paramref name="owner"/> and returns the chosen key's answer.</summary>
    public async Task<TAnswer> AskAsync(Window owner)
    {
        var result = await ShowDialog<object?>(owner);
        return result is TAnswer answer ? answer : Dismissed;
    }
}
