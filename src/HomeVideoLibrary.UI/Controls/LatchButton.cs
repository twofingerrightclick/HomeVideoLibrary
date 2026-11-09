using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Latching key that stays partly depressed and lights its LED while checked. Style class <c>red</c>
/// switches the LED from green to red.
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
