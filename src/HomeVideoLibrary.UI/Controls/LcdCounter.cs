using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>When <see cref="LcdCounter"/> shows an hours field.</summary>
public enum LcdHours
{
    /// <summary>Only while the magnitude is an hour or more; the readout widens at that point.</summary>
    Auto,
    Always,

    /// <summary>Minutes keep counting past 59 (75:00).</summary>
    Never,
}

/// <summary>
/// Seven-segment time readout behind smoked glass, like the amber VFD on a CD deck. Unlit segments
/// stay faintly visible as a fixed-width "88:88" ghost so digits never shift as the value changes.
/// </summary>
public class LcdCounter : TemplatedControl
{
    public static readonly StyledProperty<TimeSpan> ValueProperty =
        AvaloniaProperty.Register<LcdCounter, TimeSpan>(nameof(Value));

    public static readonly StyledProperty<LcdHours> HoursProperty =
        AvaloniaProperty.Register<LcdCounter, LcdHours>(nameof(Hours));

    /// <summary>Keeps a sign column so a counter that crosses zero (remaining time) does not shift.</summary>
    public static readonly StyledProperty<bool> ReserveSignProperty =
        AvaloniaProperty.Register<LcdCounter, bool>(nameof(ReserveSign));

    /// <summary>
    /// Shown instead of <see cref="Value"/> when set. Digits, '-', ':', '.', and spaces map to segments;
    /// a space is an unlit digit cell.
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LcdCounter, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> GlowProperty =
        AvaloniaProperty.Register<LcdCounter, IBrush?>(nameof(Glow));

    /// <summary>Color of the unlit segments.</summary>
    public static readonly StyledProperty<IBrush?> GhostProperty =
        AvaloniaProperty.Register<LcdCounter, IBrush?>(nameof(Ghost));

    /// <summary>Small legend printed on the glass beside the digits ("TIME", "REMAIN").</summary>
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<LcdCounter, string?>(nameof(Caption));

    public static readonly DirectProperty<LcdCounter, string> DisplayTextProperty =
        AvaloniaProperty.RegisterDirect<LcdCounter, string>(nameof(DisplayText), o => o.DisplayText);

    public static readonly DirectProperty<LcdCounter, string> GhostTextProperty =
        AvaloniaProperty.RegisterDirect<LcdCounter, string>(nameof(GhostText), o => o.GhostText);

    // DSEG fonts draw '!' as a digit-wide cell with every segment off; a plain space is narrower.
    private const char BlankCell = '!';

    private string _displayText = "";
    private string _ghostText = "";

    static LcdCounter()
    {
        ValueProperty.Changed.AddClassHandler<LcdCounter>((o, _) => o.UpdateText());
        HoursProperty.Changed.AddClassHandler<LcdCounter>((o, _) => o.UpdateText());
        ReserveSignProperty.Changed.AddClassHandler<LcdCounter>((o, _) => o.UpdateText());
        TextProperty.Changed.AddClassHandler<LcdCounter>((o, _) => o.UpdateText());
    }

    public LcdCounter() => UpdateText();

    public TimeSpan Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public LcdHours Hours
    {
        get => GetValue(HoursProperty);
        set => SetValue(HoursProperty, value);
    }

    public bool ReserveSign
    {
        get => GetValue(ReserveSignProperty);
        set => SetValue(ReserveSignProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? Glow
    {
        get => GetValue(GlowProperty);
        set => SetValue(GlowProperty, value);
    }

    public IBrush? Ghost
    {
        get => GetValue(GhostProperty);
        set => SetValue(GhostProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>The lit segments, in DSEG glyphs.</summary>
    public string DisplayText
    {
        get => _displayText;
        private set => SetAndRaise(DisplayTextProperty, ref _displayText, value);
    }

    /// <summary>Every segment of each cell in <see cref="DisplayText"/>, drawn unlit behind it.</summary>
    public string GhostText
    {
        get => _ghostText;
        private set => SetAndRaise(GhostTextProperty, ref _ghostText, value);
    }

    /// <summary>
    /// Formats <paramref name="value"/> as [H:]MM:SS (seconds truncated), with a leading '-' when negative
    /// or a blank sign cell when <paramref name="reserveSign"/> is set.
    /// </summary>
    public static string Format(TimeSpan value, LcdHours hours = LcdHours.Auto, bool reserveSign = false)
    {
        var totalSeconds = Math.Abs(value.Ticks / TimeSpan.TicksPerSecond);
        var negative = value < TimeSpan.Zero;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var showHours = hours == LcdHours.Always || (hours == LcdHours.Auto && totalMinutes >= 60);

        var text = showHours
            ? string.Create(CultureInfo.InvariantCulture, $"{totalMinutes / 60}:{totalMinutes % 60:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{totalMinutes:00}:{seconds:00}");

        if (negative)
        {
            return "-" + text;
        }

        return reserveSign ? " " + text : text;
    }

    /// <summary>
    /// Maps readout text to DSEG glyphs and the matching all-segments ghost: digits and '-' become '8',
    /// spaces become blank cells, and ':' / '.' stay put so ghost and lit text have identical widths.
    /// </summary>
    public static (string Lit, string Ghost) ToSegments(string text)
    {
        var lit = new StringBuilder(text.Length);
        var ghost = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case ':' or '.':
                    lit.Append(ch);
                    ghost.Append(ch);
                    break;
                case ' ' or BlankCell:
                    lit.Append(BlankCell);
                    ghost.Append('8');
                    break;
                default:
                    lit.Append(ch);
                    ghost.Append('8');
                    break;
            }
        }

        return (lit.ToString(), ghost.ToString());
    }

    private void UpdateText()
    {
        var (lit, ghost) = ToSegments(Text ?? Format(Value, Hours, ReserveSign));
        DisplayText = lit;
        GhostText = ghost;
    }
}
