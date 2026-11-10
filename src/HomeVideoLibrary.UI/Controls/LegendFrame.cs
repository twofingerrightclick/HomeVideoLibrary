using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>Silkscreened legend printed on the chassis above (or below) a control or group of controls.</summary>
public class LegendFrame : ContentControl
{
    public static readonly StyledProperty<string?> LegendProperty =
        AvaloniaProperty.Register<LegendFrame, string?>(nameof(Legend));

    /// <summary>Optional printed dot before the legend, as Sony uses for REC and CH/TRACKING.</summary>
    public static readonly StyledProperty<IBrush?> LegendDotProperty =
        AvaloniaProperty.Register<LegendFrame, IBrush?>(nameof(LegendDot));

    public static readonly StyledProperty<Dock> LegendPlacementProperty =
        AvaloniaProperty.Register<LegendFrame, Dock>(nameof(LegendPlacement), Dock.Top);

    public string? Legend
    {
        get => GetValue(LegendProperty);
        set => SetValue(LegendProperty, value);
    }

    public IBrush? LegendDot
    {
        get => GetValue(LegendDotProperty);
        set => SetValue(LegendDotProperty, value);
    }

    public Dock LegendPlacement
    {
        get => GetValue(LegendPlacementProperty);
        set => SetValue(LegendPlacementProperty, value);
    }
}
