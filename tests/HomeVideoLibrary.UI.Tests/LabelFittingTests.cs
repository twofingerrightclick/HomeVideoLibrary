using HomeVideoLibrary.UI.Logic;
using Xunit;

namespace HomeVideoLibrary.UI.Tests;

public class LabelFittingTests
{
    private const double SlotWidth = 300;

    // A monospaced stand-in for the handwriting face: every character half an em wide, lines 1.25 em tall, wrapped at
    // spaces.
    private static Func<double, TextMeasure> Mono(string text) => size =>
    {
        var lines = new List<int>();
        foreach (var word in text.Split(' '))
        {
            if (lines.Count > 0 && (lines[^1] + 1 + word.Length) * size / 2 <= SlotWidth)
            {
                lines[^1] += 1 + word.Length;
            }
            else
            {
                lines.Add(word.Length);
            }
        }

        return new TextMeasure(lines.Count, lines.Max() * size / 2, lines.Count * size * 1.25);
    };

    private static LabelFit Fit(string text, double slotHeight, double minSize = 20) =>
        LabelFitting.Fit(SlotWidth, slotHeight, maxSize: 27, minSize, step: 1, Mono(text));

    [Fact]
    public void A_short_title_keeps_the_design_size_on_one_line()
    {
        Assert.Equal(new LabelFit(27, 1), Fit("Christmas 1991", slotHeight: 60));
    }

    [Fact]
    public void A_title_slightly_too_long_shrinks_until_it_fits_one_line()
    {
        // 24 characters: 12 em wide, so one line fits at 25.
        Assert.Equal(new LabelFit(25, 1), Fit("Road trip to Grandma's 9", slotHeight: 60));
    }

    [Fact]
    public void A_title_too_long_for_one_line_at_the_floor_wraps_to_two_at_the_largest_size_that_fits()
    {
        // 39 characters are 19.5 em; the slot holds 15 em at the floor. Two lines are 2.5 em tall, so 24 is the largest
        // size whose two lines fit 60.
        Assert.Equal(new LabelFit(24, 2), Fit("Grandpa's seventieth birthday at the la", slotHeight: 60));
    }

    [Fact]
    public void A_title_too_long_for_two_lines_sits_at_the_floor_on_two_trimmed_lines()
    {
        var fit = Fit(string.Join(' ', Enumerable.Repeat("summer camp", 12)), slotHeight: 60);

        Assert.Equal(new LabelFit(20, 2), fit);
    }

    [Fact]
    public void A_slot_too_short_for_two_lines_at_the_floor_trims_to_one()
    {
        // Two lines at 20 are 50 tall.
        var fit = Fit(string.Join(' ', Enumerable.Repeat("summer camp", 12)), slotHeight: 42);

        Assert.Equal(new LabelFit(20, 1), fit);
    }

    [Fact]
    public void A_floor_above_the_design_size_keeps_the_design_size()
    {
        Assert.Equal(new LabelFit(27, 1), Fit("Christmas 1991", slotHeight: 60, minSize: 30));
    }
}
