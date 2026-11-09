using Avalonia;
using Avalonia.Controls;

namespace HomeVideoLibrary.Gallery;

/// <summary>
/// Pins interaction pseudo-classes (":pointerover :pressed :focus-visible") on a gallery specimen so
/// every state can be inspected side by side without a pointer.
/// </summary>
public sealed class ForcedState
{
    public static readonly AttachedProperty<string?> StatesProperty =
        AvaloniaProperty.RegisterAttached<ForcedState, Control, string?>("States");

    static ForcedState()
    {
        StatesProperty.Changed.AddClassHandler<Control>(OnStatesChanged);
    }

    private ForcedState()
    {
    }

    public static string? GetStates(Control control) => control.GetValue(StatesProperty);

    public static void SetStates(Control control, string? value) => control.SetValue(StatesProperty, value);

    private static void OnStatesChanged(Control control, AvaloniaPropertyChangedEventArgs e)
    {
        var pseudoClasses = (IPseudoClasses)control.Classes;
        foreach (var state in Split(e.OldValue as string))
        {
            pseudoClasses.Remove(state);
        }

        foreach (var state in Split(e.NewValue as string))
        {
            pseudoClasses.Add(state);
        }
    }

    private static string[] Split(string? states) =>
        states?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
