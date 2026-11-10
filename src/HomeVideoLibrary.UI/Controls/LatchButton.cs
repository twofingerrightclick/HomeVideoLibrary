using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Latching key for on/off modes (loop, mute, panel visibility) that stays partly depressed and lights
/// its LED while checked. Stock <c>ToggleButton</c> gets the same look. Style classes: <c>red</c> LED
/// for record/armed states (default green), sizes <c>toolbar</c> and <c>icon</c>.
/// </summary>
public class LatchButton : ToggleButton
{
    public static readonly StyledProperty<Geometry?> IconProperty = Vcr.IconProperty.AddOwner<LatchButton>();

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
