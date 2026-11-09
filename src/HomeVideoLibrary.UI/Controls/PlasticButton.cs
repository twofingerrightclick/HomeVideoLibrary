using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Hard molded key cap sitting in a recessed well. Style classes: <c>transport</c>, <c>toolbar</c>,
/// <c>icon</c> (square), and accents <c>primary</c>, <c>destructive</c>, <c>go</c>.
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
