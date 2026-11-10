using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HomeVideoLibrary.Desktop.Views.Editing;

/// <summary>The video page's EDIT tab. The in/out fields commit when they lose focus, or on Enter, which
/// also moves to the next field.</summary>
public sealed partial class EditTab : UserControl
{
    public EditTab()
    {
        InitializeComponent();
        InField.AddHandler(KeyDownEvent, CommitOnEnter, RoutingStrategies.Tunnel);
        OutField.AddHandler(KeyDownEvent, CommitOnEnter, RoutingStrategies.Tunnel);
    }

    private static void CommitOnEnter(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || sender is not TextBox field) return;
        BindingOperations.GetBindingExpressionBase(field, TextBox.TextProperty)?.UpdateSource();
        KeyboardNavigationHandler.GetNext(field, NavigationDirection.Next)?.Focus(NavigationMethod.Tab);
        args.Handled = true;
    }
}
