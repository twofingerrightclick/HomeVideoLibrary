using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>Small attached behaviors for text fields created on demand (inline renames).</summary>
public static class FieldBehaviors
{
    /// <summary>Focuses the field and selects its text when it enters the tree, so an inline rename can be typed over.</summary>
    public static readonly AttachedProperty<bool> FocusOnAttachProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("FocusOnAttach", typeof(FieldBehaviors));

    /// <summary>Executed when the field loses focus (for example, to commit an inline rename by clicking away).</summary>
    public static readonly AttachedProperty<ICommand?> LostFocusCommandProperty =
        AvaloniaProperty.RegisterAttached<TextBox, ICommand?>("LostFocusCommand", typeof(FieldBehaviors));

    static FieldBehaviors()
    {
        FocusOnAttachProperty.Changed.AddClassHandler<TextBox>((field, args) =>
        {
            if (args.NewValue is true) field.AttachedToVisualTree += FocusAndSelect;
            else field.AttachedToVisualTree -= FocusAndSelect;
        });
        InputElement.LostFocusEvent.AddClassHandler<TextBox>((field, _) =>
        {
            if (GetLostFocusCommand(field) is { } command && command.CanExecute(null)) command.Execute(null);
        }, RoutingStrategies.Bubble);
    }

    public static bool GetFocusOnAttach(TextBox field) => field.GetValue(FocusOnAttachProperty);
    public static void SetFocusOnAttach(TextBox field, bool value) => field.SetValue(FocusOnAttachProperty, value);
    public static ICommand? GetLostFocusCommand(TextBox field) => field.GetValue(LostFocusCommandProperty);
    public static void SetLostFocusCommand(TextBox field, ICommand? value) => field.SetValue(LostFocusCommandProperty, value);

    private static void FocusAndSelect(object? sender, VisualTreeAttachmentEventArgs args)
    {
        var field = (TextBox)sender!;
        field.Focus(NavigationMethod.Tab);
        field.SelectAll();
    }
}
