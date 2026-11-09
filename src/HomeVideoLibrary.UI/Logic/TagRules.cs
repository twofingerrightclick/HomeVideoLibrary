using System.Text;

namespace HomeVideoLibrary.UI.Logic;

/// <summary>How often a tag is used across the library.</summary>
public sealed record TagFrequency(string Tag, int Count);

/// <summary>Where a query matched inside a tag; <see cref="Start"/> is -1 when it did not match.</summary>
public readonly record struct TagMatch(int Start, int Length, TagMatchKind Kind)
{
    public static TagMatch None { get; } = new(-1, 0, TagMatchKind.None);

    public bool IsMatch => Kind != TagMatchKind.None;
}

/// <summary>Match tiers, best first.</summary>
public enum TagMatchKind
{
    Prefix,
    WordStart,
    Substring,
    None,
}

/// <summary>Tag entry rules. Tags compare case-insensitively everywhere.</summary>
public static class TagRules
{
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Trims and collapses internal whitespace runs to one space; returns null when nothing remains.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Characters that end a tag: ASCII comma, the full-width and ideographic commas CJK input methods
    /// produce, and line breaks so a pasted one-per-line list splits too.
    /// </summary>
    public static IReadOnlyList<char> Separators { get; } = [',', '\uFF0C', '\u3001', '\n', '\r'];

    /// <summary>Splits on <see cref="Separators"/>, normalizes each piece, and drops empty pieces and case-insensitive repeats.</summary>
    public static IReadOnlyList<string> SplitEntry(string? raw) =>
        raw is null ? [] : Distinct(raw.Split([.. Separators]).Select(Normalize).OfType<string>());

    /// <summary>Index of the last separator in <paramref name="text"/>, or -1.</summary>
    public static int LastSeparatorIndex(string text) => text.LastIndexOfAny([.. Separators]);

    public static bool Contains(IEnumerable<string> tags, string tag) => tags.Contains(tag, Comparer);

    /// <summary>Keeps the first occurrence of each tag, ignoring case.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string> tags) => tags.Distinct(Comparer).ToList();

    /// <summary>Finds the best match of an already-normalized query: a prefix, else the start of a word, else any substring.</summary>
    public static TagMatch FindMatch(string tag, string query)
    {
        if (query.Length == 0)
        {
            return TagMatch.None;
        }

        var first = tag.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (first < 0)
        {
            return TagMatch.None;
        }

        if (first == 0)
        {
            return new TagMatch(0, query.Length, TagMatchKind.Prefix);
        }

        for (var index = first; index >= 0; index = tag.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (IsWordSeparator(tag[index - 1]))
            {
                return new TagMatch(index, query.Length, TagMatchKind.WordStart);
            }
        }

        return new TagMatch(first, query.Length, TagMatchKind.Substring);
    }

    /// <summary>
    /// Ranks library tags for the entry text. Tags already chosen are excluded, and source entries that
    /// differ only by case are merged (counts summed, most-used casing kept). An empty query ranks every
    /// tag by count; otherwise only matches are kept, ordered by <see cref="TagMatchKind"/> tier. Ties
    /// sort by count descending, then alphabetically.
    /// </summary>
    public static IReadOnlyList<TagFrequency> RankSuggestions(
        string? query, IEnumerable<TagFrequency> source, IEnumerable<string> chosen, int max)
    {
        if (max <= 0)
        {
            return [];
        }

        var normalizedQuery = Normalize(query) ?? string.Empty;
        var excluded = new HashSet<string>(chosen, Comparer);
        var candidates = MergeCase(source)
            .Where(t => !excluded.Contains(t.Tag))
            .Select(t => (Frequency: t, Tier: normalizedQuery.Length == 0 ? TagMatchKind.Prefix : FindMatch(t.Tag, normalizedQuery).Kind))
            .Where(c => c.Tier != TagMatchKind.None);

        return candidates
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.Frequency.Count)
            .ThenBy(c => c.Frequency.Tag, Comparer)
            .ThenBy(c => c.Frequency.Tag, StringComparer.Ordinal)
            .Take(max)
            .Select(c => c.Frequency)
            .ToList();
    }

    /// <summary>Merges entries that differ only by case; the casing with the highest count wins, first seen on ties.</summary>
    public static IReadOnlyList<TagFrequency> MergeCase(IEnumerable<TagFrequency> source)
    {
        var merged = new Dictionary<string, (string Casing, int CasingCount, int Total)>(Comparer);
        var order = new List<string>();
        foreach (var entry in source)
        {
            var tag = Normalize(entry.Tag);
            if (tag is null)
            {
                continue;
            }

            if (merged.TryGetValue(tag, out var existing))
            {
                var keepExisting = existing.CasingCount >= entry.Count;
                merged[tag] = (keepExisting ? existing.Casing : tag, keepExisting ? existing.CasingCount : entry.Count, existing.Total + entry.Count);
            }
            else
            {
                merged[tag] = (tag, entry.Count, entry.Count);
                order.Add(tag);
            }
        }

        return order.Select(key => merged[key]).Select(m => new TagFrequency(m.Casing, m.Total)).ToList();
    }

    private static bool IsWordSeparator(char ch) => ch is ' ' or '-' or '_';
}
