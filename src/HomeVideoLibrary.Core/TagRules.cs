using System.Globalization;
using System.Text;

namespace HomeVideoLibrary.Core;

/// <summary>The one definition of what a tag is. The MKV reader and writer, the tag index and the chip input all use it,
/// so a tag read from a file, typed as a chip and looked up in the index is the same tag.</summary>
public static class TagRules
{
    /// <summary>Characters that end a tag: the Matroska KEYWORDS comma, the full-width and ideographic commas that CJK
    /// input methods produce, and line breaks so a pasted one-per-line list splits too.</summary>
    public static IReadOnlyList<char> Separators { get; } = [',', '，', '、', '\n', '\r'];

    private static readonly char[] SeparatorArray = [.. Separators];

    /// <summary>Tag identity: equal when their <see cref="Key"/>s are equal, so case and diacritics never make two tags.</summary>
    public static IEqualityComparer<string> Comparer { get; } = new KeyComparer();

    /// <summary>The spelling to store: trimmed, with whitespace runs collapsed to one space; null when nothing remains.
    /// Separators are not removed; use <see cref="Split"/> for raw input.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>The identity of a tag: normalized, then case- and diacritic-folded ("Été" and "ete" share a key).</summary>
    public static string Key(string tag)
    {
        var decomposed = (Normalize(tag) ?? "").Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    /// <summary>Splits raw input on <see cref="Separators"/>, normalizes each piece, and drops empty pieces and repeats.</summary>
    public static IReadOnlyList<string> Split(string? raw) => raw is null ? [] : Distinct(raw.Split(SeparatorArray).Select(Normalize).OfType<string>());

    /// <summary>Canonical tags from any number of raw values: each is split and normalized, and repeats are dropped,
    /// keeping the first spelling.</summary>
    public static IReadOnlyList<string> Canonicalize(IEnumerable<string> raw) => Distinct(raw.SelectMany(Split));

    /// <summary>Keeps the first occurrence of each tag by <see cref="Comparer"/>.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> tags) => tags.Distinct(Comparer).ToArray();

    public static bool Contains(IEnumerable<string> tags, string tag) => tags.Contains(tag, Comparer);

    /// <summary>True when the tag, or a word in it, starts with <paramref name="prefix"/> under tag identity. An empty
    /// prefix matches every tag.</summary>
    public static bool MatchesPrefix(string tag, string? prefix)
    {
        var key = Key(tag);
        var folded = Key(prefix ?? "");
        if (folded.Length == 0) return true;
        for (var index = key.IndexOf(folded, StringComparison.Ordinal); index >= 0; index = key.IndexOf(folded, index + 1, StringComparison.Ordinal))
            if (index == 0 || !char.IsLetterOrDigit(key[index - 1])) return true;
        return false;
    }

    private sealed class KeyComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => x is null || y is null ? x is null && y is null : Key(x) == Key(y);
        public int GetHashCode(string obj) => StringComparer.Ordinal.GetHashCode(Key(obj));
    }
}
