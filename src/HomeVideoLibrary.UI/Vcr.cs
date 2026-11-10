using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.UI;

/// <summary>
/// Attached properties shared by every VCR control template. They work on stock controls too
/// (<c>Button</c>, <c>TabItem</c>, <c>MenuItem</c>), so the app can opt existing markup into icons and
/// press sounds without switching control types.
/// </summary>
public sealed class Vcr
{
    /// <summary>Printed glyph shown by templates that support an icon, drawn with the legend color.</summary>
    public static readonly AttachedProperty<Geometry?> IconProperty =
        AvaloniaProperty.RegisterAttached<Vcr, Control, Geometry?>("Icon");

    public static readonly AttachedProperty<double> IconSizeProperty =
        AvaloniaProperty.RegisterAttached<Vcr, Control, double>("IconSize", 14);

    /// <summary>Feedback played when the control is pressed by pointer, Space, or Enter.</summary>
    public static readonly AttachedProperty<ClickSoundKind> SoundProperty =
        AvaloniaProperty.RegisterAttached<Vcr, Control, ClickSoundKind>("Sound");

    private static RoutedEventArgs? _lastSounded;
    private static Key? _heldKey;

    static Vcr()
    {
        InputElement.PointerPressedEvent.AddClassHandler<Control>(OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<Control>(OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.KeyUpEvent.AddClassHandler<Control>(OnKeyUp, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private Vcr()
    {
    }

    public static Geometry? GetIcon(Control control) => control.GetValue(IconProperty);

    public static void SetIcon(Control control, Geometry? value) => control.SetValue(IconProperty, value);

    public static double GetIconSize(Control control) => control.GetValue(IconSizeProperty);

    public static void SetIconSize(Control control, double value) => control.SetValue(IconSizeProperty, value);

    public static ClickSoundKind GetSound(Control control) => control.GetValue(SoundProperty);

    public static void SetSound(Control control, ClickSoundKind value) => control.SetValue(SoundProperty, value);

    private static void OnPointerPressed(Control control, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            PlayOnce(control, e);
        }
    }

    // Only buttons activate from the keyboard (tabs select with arrows), and auto-repeat while a key is
    // held must not re-click.
    private static void OnKeyDown(Control control, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter) || control is not Button || !control.IsFocused)
        {
            return;
        }

        if (_heldKey == e.Key)
        {
            return;
        }

        _heldKey = e.Key;
        PlayOnce(control, e);
    }

    private static void OnKeyUp(Control control, KeyEventArgs e)
    {
        if (_heldKey == e.Key)
        {
            _heldKey = null;
        }
    }

    // A press bubbles through every ancestor; only the innermost control with a sound plays it.
    private static void PlayOnce(Control control, RoutedEventArgs e)
    {
        var kind = control.GetValue(SoundProperty);
        if (kind == ClickSoundKind.None || ReferenceEquals(_lastSounded, e) || !control.IsEffectivelyEnabled)
        {
            return;
        }

        _lastSounded = e;
        ClickSounds.Service.Play(kind);
    }
}
