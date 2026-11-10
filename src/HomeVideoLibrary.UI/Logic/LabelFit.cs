namespace HomeVideoLibrary.UI.Logic;

/// <summary>The size and line count a label is set at; text that still overflows is trimmed with an ellipsis.</summary>
public readonly record struct LabelFit(double FontSize, int MaxLines);

/// <summary>Text laid out at a font size and wrapped at the slot width: its line count, widest line and total height.</summary>
public readonly record struct TextMeasure(int Lines, double Width, double Height);

public static class LabelFitting
{
    /// <summary>
    /// Fits a label into a slot, preferring in order: one line at the largest size from <paramref name="maxSize"/> down to
    /// <paramref name="minSize"/> (in <paramref name="step"/> decrements, ending exactly at the floor); two lines at the
    /// largest such size; the floor size on as many lines (two at most) as the slot height holds, trimmed.
    /// <paramref name="measure"/> lays the text out at a size, wrapped at <paramref name="slotWidth"/>.
    /// </summary>
    public static LabelFit Fit(double slotWidth, double slotHeight, double maxSize, double minSize, double step, Func<double, TextMeasure> measure)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        minSize = Math.Min(minSize, maxSize);
        var sizes = new List<double>();
        for (var size = maxSize; size - minSize > step / 1000; size -= step)
        {
            sizes.Add(size);
        }

        sizes.Add(minSize);

        var measures = new TextMeasure?[sizes.Count];
        for (var i = 0; i < sizes.Count; i++)
        {
            if (Fits(Measure(i), 1))
            {
                return new LabelFit(sizes[i], 1);
            }
        }

        for (var i = 0; i < sizes.Count; i++)
        {
            if (Fits(Measure(i), 2))
            {
                return new LabelFit(sizes[i], 2);
            }
        }

        var floor = Measure(sizes.Count - 1);
        var lineHeight = floor.Height / Math.Max(1, floor.Lines);
        return new LabelFit(minSize, 2 * lineHeight <= slotHeight ? 2 : 1);

        TextMeasure Measure(int i) => measures[i] ??= measure(sizes[i]);

        bool Fits(TextMeasure m, int lines) => m.Lines <= lines && m.Width <= slotWidth && m.Height <= slotHeight;
    }
}
