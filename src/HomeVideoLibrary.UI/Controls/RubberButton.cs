using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Soft matte remote-control key poking through a gasket. Style classes: <c>round</c> (circular),
/// and dome colors <c>red</c>, <c>green</c>, <c>yellow</c>.
/// </summary>
public class RubberButton : Button
{
    public static readonly StyledProperty<Geometry?> IconProperty = Vcr.IconProperty.AddOwner<RubberButton>();

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
