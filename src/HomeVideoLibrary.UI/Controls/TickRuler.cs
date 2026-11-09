using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Fine scale printed on both sides of a fader groove: evenly spaced hairline ticks mirrored about the
/// center line, with a longer tick every <see cref="MajorEvery"/> divisions. Drawn in one pass so a
/// dense scale costs no visual tree.
/// </summary>
public class TickRuler : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<TickRuler, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<TickRuler, Orientation>(nameof(Orientation), Orientation.Horizontal);

    /// <summary>Number of intervals between the first and last tick.</summary>
    public static readonly StyledProperty<int> DivisionsProperty =
        AvaloniaProperty.Register<TickRuler, int>(nameof(Divisions), 40, validate: d => d > 0);

    /// <summary>Every Nth tick (and both end ticks) is drawn at <see cref="MajorLength"/>.</summary>
    public static readonly StyledProperty<int> MajorEveryProperty =
        AvaloniaProperty.Register<TickRuler, int>(nameof(MajorEvery), 10, validate: n => n > 0);

    /// <summary>Distance from the center line to the inner end of each tick, leaving room for the groove.</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<TickRuler, double>(nameof(Gap), 6);

    public static readonly StyledProperty<double> MinorLengthProperty =
        AvaloniaProperty.Register<TickRuler, double>(nameof(MinorLength), 2);

    public static readonly StyledProperty<double> MajorLengthProperty =
        AvaloniaProperty.Register<TickRuler, double>(nameof(MajorLength), 4);

    // Closer than this, minor ticks read as a solid band and pixel rounding makes their gaps visibly uneven.
    private const double MinMinorSpacing = 4.5;

    static TickRuler()
    {
        AffectsRender<TickRuler>(
            ForegroundProperty,
            OrientationProperty,
            DivisionsProperty,
            MajorEveryProperty,
            GapProperty,
            MinorLengthProperty,
            MajorLengthProperty);
        AffectsMeasure<TickRuler>(OrientationProperty, GapProperty, MajorLengthProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public int Divisions
    {
        get => GetValue(DivisionsProperty);
        set => SetValue(DivisionsProperty, value);
    }

    public int MajorEvery
    {
        get => GetValue(MajorEveryProperty);
        set => SetValue(MajorEveryProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public double MinorLength
    {
        get => GetValue(MinorLengthProperty);
        set => SetValue(MinorLengthProperty, value);
    }

    public double MajorLength
    {
        get => GetValue(MajorLengthProperty);
        set => SetValue(MajorLengthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var depth = 2 * (Gap + MajorLength);
        return Orientation == Orientation.Horizontal ? new Size(0, depth) : new Size(depth, 0);
    }

    public override void Render(DrawingContext context)
    {
        var brush = Foreground;
        if (brush is null)
        {
            return;
        }

        var horizontal = Orientation == Orientation.Horizontal;
        var length = horizontal ? Bounds.Width : Bounds.Height;
        var center = (horizontal ? Bounds.Height : Bounds.Width) / 2;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

        // One whole device pixel wide, with its leading edge on a device pixel, keeps every tick crisp.
        var hairline = Math.Max(1, Math.Round(scale)) / scale;
        var spacing = (length - hairline) / Divisions;

        // Thin the minors by halves while that keeps them landing between majors.
        var minorStep = 1;
        while (spacing * minorStep < MinMinorSpacing && MajorEvery % (minorStep * 2) == 0)
        {
            minorStep *= 2;
        }

        var drawMinors = spacing * minorStep >= MinMinorSpacing;

        for (var i = 0; i <= Divisions; i++)
        {
            var major = i % MajorEvery == 0 || i == Divisions;
            if (!major && (!drawMinors || i % minorStep != 0))
            {
                continue;
            }

            var along = Math.Round(i * spacing * scale) / scale;
            var tick = major ? MajorLength : MinorLength;
            var near = Snap(center - Gap - tick, scale);
            var far = Snap(center + Gap, scale);
            tick = Snap(tick, scale);

            if (horizontal)
            {
                context.FillRectangle(brush, new Rect(along, near, hairline, tick));
                context.FillRectangle(brush, new Rect(along, far, hairline, tick));
            }
            else
            {
                context.FillRectangle(brush, new Rect(near, along, tick, hairline));
                context.FillRectangle(brush, new Rect(far, along, tick, hairline));
            }
        }
    }

    private static double Snap(double value, double scale) => Math.Round(value * scale) / scale;
}
