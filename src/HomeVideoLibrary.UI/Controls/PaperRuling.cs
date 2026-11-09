using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

public enum RulingKind
{
    None,
    Lined,
    Grid,
}

/// <summary>Draws pixel-snapped ruled or graph-paper lines across label stock.</summary>
public class PaperRuling : Control
{
    public static readonly StyledProperty<RulingKind> KindProperty =
        AvaloniaProperty.Register<PaperRuling, RulingKind>(nameof(Kind), RulingKind.Lined);

    public static readonly StyledProperty<IBrush?> RuleBrushProperty =
        AvaloniaProperty.Register<PaperRuling, IBrush?>(nameof(RuleBrush));

    /// <summary>Distance between rules in device-independent pixels.</summary>
    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<PaperRuling, double>(nameof(Pitch), 14);

    static PaperRuling()
    {
        AffectsRender<PaperRuling>(KindProperty, RuleBrushProperty, PitchProperty);
    }

    public RulingKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? RuleBrush
    {
        get => GetValue(RuleBrushProperty);
        set => SetValue(RuleBrushProperty, value);
    }

    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Kind == RulingKind.None || RuleBrush is null || Pitch < 2)
        {
            return;
        }

        var pen = new Pen(RuleBrush, 1);
        var size = Bounds.Size;
        var pitch = Kind == RulingKind.Grid ? Pitch / 2 : Pitch;
        for (var y = pitch; y < size.Height - 1; y += pitch)
        {
            var snapped = Math.Round(y) + 0.5;
            context.DrawLine(pen, new Point(0, snapped), new Point(size.Width, snapped));
        }

        if (Kind != RulingKind.Grid)
        {
            return;
        }

        for (var x = pitch; x < size.Width - 1; x += pitch)
        {
            var snapped = Math.Round(x) + 0.5;
            context.DrawLine(pen, new Point(snapped, 0), new Point(snapped, size.Height));
        }
    }
}
