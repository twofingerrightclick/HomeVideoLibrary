namespace HomeVideoLibrary.Core;

public static class Timecode
{
    // Keeps the largest accepted value (999999 hours) far inside TimeSpan without overflow checks.
    private const int MaxIntegerDigits = 6;

    /// <summary>Parses <c>ss[.f]</c>, <c>m:ss[.f]</c> or <c>h:mm:ss[.f]</c>. The leading component may exceed 59
    /// ("90" is 90 seconds); later components must be below 60. Fractions beyond 100 ns are truncated.</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        var dot = trimmed.IndexOf('.');
        var whole = dot < 0 ? trimmed : trimmed[..dot];
        var fraction = dot < 0 ? "" : trimmed[(dot + 1)..];
        if (dot >= 0 && (fraction.Length == 0 || !fraction.All(char.IsAsciiDigit))) return false;

        var parts = whole.Split(':');
        if (parts.Length > 3) return false;
        long ticks = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var leading = i == 0;
            // ".5" omits the seconds; any later component must be present.
            if (part.Length == 0 && !(leading && parts.Length == 1 && fraction.Length > 0)) return false;
            if (part.Length > MaxIntegerDigits || !part.All(char.IsAsciiDigit)) return false;
            var number = part.Length == 0 ? 0 : long.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (!leading && (part.Length > 2 || number >= 60)) return false;
            var unit = (parts.Length - i) switch
            {
                3 => TimeSpan.TicksPerHour,
                2 => TimeSpan.TicksPerMinute,
                _ => TimeSpan.TicksPerSecond,
            };
            ticks += number * unit;
        }
        if (fraction.Length > 0)
            ticks += long.Parse(fraction.Length >= 7 ? fraction[..7] : fraction.PadRight(7, '0'),
                System.Globalization.CultureInfo.InvariantCulture);
        value = TimeSpan.FromTicks(ticks);
        return true;
    }

    /// <summary>Formats as <c>h:mm:ss.fff</c>; hours are not padded and may exceed 23.</summary>
    public static string Format(TimeSpan value) =>
        $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}
