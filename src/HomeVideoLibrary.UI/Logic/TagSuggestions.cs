using CoreTagRules = HomeVideoLibrary.Core.TagRules;

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

/// <summary>
/// Supplies ranked suggestions for a <see cref="Controls.TagChipInput"/>: tags matching
/// <paramref name="query"/> (null lists the most used), excluding <paramref name="exclude"/>, at most
/// <paramref name="max"/>, best first, in their canonical spelling. The app backs this with its tag index.
/// </summary>
public delegate IReadOnlyList<TagFrequency> TagSuggestionProvider(string? query, IReadOnlyCollection<string> exclude, int max);

/// <summary>
/// Suggestion matching and a snapshot-backed ranking for hosts without a tag index (the gallery, tests).
/// What a tag is (normalization, identity, separators) is defined by <see cref="CoreTagRules"/>.
/// </summary>
public static class TagSuggestions
{
    /// <summary>A provider that ranks a fixed snapshot with <see cref="Rank"/>.</summary>
    public static TagSuggestionProvider FromSnapshot(IReadOnlyList<TagFrequency> snapshot) =>
        (query, exclude, max) => Rank(query, snapshot, exclude, max);

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
    public static IReadOnlyList<TagFrequency> Rank(
        string? query, IEnumerable<TagFrequency> source, IEnumerable<string> chosen, int max)
    {
        if (max <= 0)
        {
            return [];
        }

        var normalizedQuery = CoreTagRules.Normalize(query) ?? string.Empty;
        var excluded = new HashSet<string>(chosen, CoreTagRules.Comparer);
        var candidates = MergeCase(source)
            .Where(t => !excluded.Contains(t.Tag))
            .Select(t => (Frequency: t, Tier: normalizedQuery.Length == 0 ? TagMatchKind.Prefix : FindMatch(t.Tag, normalizedQuery).Kind))
            .Where(c => c.Tier != TagMatchKind.None);

        return candidates
            .OrderBy(c => c.Tier)
            .ThenByDescending(c => c.Frequency.Count)
            .ThenBy(c => CoreTagRules.Key(c.Frequency.Tag), StringComparer.Ordinal)
            .ThenBy(c => c.Frequency.Tag, StringComparer.Ordinal)
            .Take(max)
            .Select(c => c.Frequency)
            .ToList();
    }

    /// <summary>Merges entries that differ only by case; the casing with the highest count wins, first seen on ties.</summary>
    public static IReadOnlyList<TagFrequency> MergeCase(IEnumerable<TagFrequency> source)
    {
        var merged = new Dictionary<string, (string Casing, int CasingCount, int Total)>(CoreTagRules.Comparer);
        var order = new List<string>();
        foreach (var entry in source)
        {
            var tag = CoreTagRules.Normalize(entry.Tag);
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
