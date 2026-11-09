using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Hard molded key cap sitting in a recessed well: the default command button. Stock <c>Button</c>
/// gets the same look. Style classes: sizes <c>transport</c> (large) and <c>toolbar</c> (small),
/// <c>icon</c> (square, icon-only), and accents <c>primary</c>, <c>destructive</c>, <c>go</c>.
/// </summary>
public class PlasticButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty = Vcr.IconProperty.AddOwner<PlasticButton>();

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
