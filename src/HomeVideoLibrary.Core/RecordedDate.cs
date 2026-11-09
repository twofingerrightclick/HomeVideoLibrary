using System.Globalization;

namespace HomeVideoLibrary.Core;

/// <summary>DATE_RECORDED values, which Matroska allows to be partial ("1997", "1997-07").</summary>
public static class RecordedDate
{
    /// <summary>Parses a full or partial date. Partial dates resolve to their first day; values without an offset are UTC.</summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, ["yyyy", "yyyy-MM", "yyyy-MM-dd"], CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            value = new DateTimeOffset(date, TimeSpan.Zero);
            return true;
        }
        return DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    /// <summary>True when the text carries a time of day rather than only a (possibly partial) date.</summary>
    public static bool HasTimeOfDay(string? text) => TryParse(text, out _) && text!.Trim().Contains(':');

    /// <summary>The DATE_RECORDED text to write: the raw value when it still means <see cref="VideoMetadata.RecordedAt"/>,
    /// otherwise <see cref="VideoMetadata.RecordedAt"/> in round-trip format, or empty when there is no date.</summary>
    public static string ToTagText(VideoMetadata metadata)
    {
        var raw = metadata.RecordedAtText.Trim();
        DateTimeOffset? parsed = TryParse(raw, out var value) ? value : null;
        if (raw.Length > 0 && parsed == metadata.RecordedAt) return raw;
        return metadata.RecordedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    /// <summary>Date-only text for editing: a partial raw value as written, otherwise <c>yyyy-MM-dd</c>.</summary>
    public static string FormatForEditing(VideoMetadata metadata)
    {
        var raw = metadata.RecordedAtText.Trim();
        if (raw.Length > 0 && TryParse(raw, out var parsed) && parsed == metadata.RecordedAt && !HasTimeOfDay(raw) && raw.Length < 10)
            return raw;
        return metadata.RecordedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    }
}
