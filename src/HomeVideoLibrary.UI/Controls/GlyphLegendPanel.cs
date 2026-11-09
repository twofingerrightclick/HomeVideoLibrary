using Avalonia;
using Avalonia.Controls;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Lays children out in a row and applies <see cref="Spacing"/> only between children that take up
/// space, so an icon-only or text-only key stays exactly centered.
/// </summary>
public class GlyphLegendPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<GlyphLegendPanel, double>(nameof(Spacing), 6);

    static GlyphLegendPanel()
    {
        AffectsMeasure<GlyphLegendPanel>(SpacingProperty);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0, height = 0;
        var visible = 0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            if (!Occupies(child))
            {
                continue;
            }

            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            visible++;
        }

        return new Size(width + Math.Max(0, visible - 1) * Spacing, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var child in Children)
        {
            if (!Occupies(child))
            {
                child.Arrange(default);
                continue;
            }

            var size = child.DesiredSize;
            child.Arrange(new Rect(x, (finalSize.Height - size.Height) / 2, size.Width, size.Height));
            x += size.Width + Spacing;
        }

        return finalSize;
    }

    private static bool Occupies(Control child) => child.IsVisible && child.DesiredSize.Width > 0;
}
