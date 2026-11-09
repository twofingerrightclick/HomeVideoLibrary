using System.Collections.Immutable;

namespace HomeVideoLibrary.Infrastructure;

/// <param name="Tag">The most used spelling of the tag.</param>
/// <param name="VideoCount">Videos tagged at video level.</param>
/// <param name="ChapterCount">Chapters carrying the tag, across all videos.</param>
/// <param name="LastUsedUtc">The latest content change of a video using the tag. It is not lowered when that video
/// later drops the tag, so after removals it can be later than the true latest use.</param>
public sealed record TagSuggestion(string Tag, int VideoCount, int ChapterCount, DateTimeOffset LastUsedUtc)
{
    public int Uses => VideoCount + ChapterCount;
}

/// <summary>An immutable index of every tag in the library with usage counts, maintained by diffing records.
/// Tags are matched case- and diacritic-insensitively ("Été" and "ete" are the same tag).</summary>
public sealed class TagIndex
{
    private readonly ImmutableDictionary<string, Entry> _entries;

    private TagIndex(ImmutableDictionary<string, Entry> entries) => _entries = entries;

    public static TagIndex Empty { get; } = new(ImmutableDictionary.Create<string, Entry>(StringComparer.Ordinal));

    public int Count => _entries.Count;

    public static TagIndex Build(IEnumerable<VideoRecord> records) => Empty.Apply(records.Select(record => ((VideoRecord?)null, (VideoRecord?)record)));

    /// <summary>Returns the index after replacing <paramref name="before"/> with <paramref name="after"/> (either may be null
    /// for an add or a remove). Returns this instance when the tags did not change.</summary>
    public TagIndex Apply(VideoRecord? before, VideoRecord? after) => Apply([(before, after)]);

    public TagIndex Apply(IEnumerable<(VideoRecord? Before, VideoRecord? After)> changes)
    {
        ImmutableDictionary<string, Entry>.Builder? builder = null;
        foreach (var (before, after) in changes)
        {
            var removed = before is null ? [] : Usage.Of(before);
            var added = after is null ? [] : Usage.Of(after);
            if (before is not null && after is not null && Usage.SameCounts(removed, added) &&
                (added.Count == 0 || after.LastModifiedUtc <= before.LastModifiedUtc)) continue;
            builder ??= _entries.ToBuilder();
            foreach (var (key, usage) in removed)
            {
                if (!builder.TryGetValue(key, out var entry)) continue;
                var next = entry.Subtract(usage);
                if (next.Uses <= 0) builder.Remove(key); else builder[key] = next;
            }
            foreach (var (key, usage) in added)
                builder[key] = builder.TryGetValue(key, out var entry) ? entry.Add(usage, after!.LastModifiedUtc) : Entry.Create(usage, after!.LastModifiedUtc);
        }
        return builder is null ? this : new TagIndex(builder.ToImmutable());
    }

    public TagSuggestion? Find(string tag) => _entries.TryGetValue(Key(tag), out var entry) ? entry.ToSuggestion() : null;

    /// <summary>Every tag, most used first.</summary>
    public IReadOnlyList<TagSuggestion> All() => Order(_entries.Values.Select(entry => entry.ToSuggestion())).ToArray();

    /// <summary>Tags whose name, or a word in it, starts with <paramref name="prefix"/>, ordered by uses, then most recent
    /// use, then name. An empty prefix suggests the most used tags. Tags in <paramref name="exclude"/> (for example,
    /// chips already on the video) are skipped.</summary>
    public IReadOnlyList<TagSuggestion> Suggest(string? prefix, int limit = 8, IEnumerable<string>? exclude = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var folded = Key(prefix ?? "");
        var excluded = (exclude ?? []).Select(Key).ToHashSet(StringComparer.Ordinal);
        var matches = _entries.Where(pair => !excluded.Contains(pair.Key) && StartsWord(pair.Key, folded))
            .Select(pair => pair.Value.ToSuggestion());
        return Order(matches).Take(limit).ToArray();
    }

    private static IOrderedEnumerable<TagSuggestion> Order(IEnumerable<TagSuggestion> suggestions) => suggestions
        .OrderByDescending(suggestion => suggestion.Uses)
        .ThenByDescending(suggestion => suggestion.LastUsedUtc)
        .ThenBy(suggestion => suggestion.Tag, StringComparer.OrdinalIgnoreCase)
        .ThenBy(suggestion => suggestion.Tag, StringComparer.Ordinal);

    private static bool StartsWord(string key, string prefix)
    {
        if (prefix.Length == 0) return true;
        for (var index = key.IndexOf(prefix, StringComparison.Ordinal); index >= 0;
             index = key.IndexOf(prefix, index + 1, StringComparison.Ordinal))
            if (index == 0 || !char.IsLetterOrDigit(key[index - 1])) return true;
        return false;
    }

    /// <summary>The identity of a tag: trimmed, case- and diacritic-folded.</summary>
    public static string Key(string tag) => LibrarySearch.Fold(tag.Trim());

    private sealed record Usage(int Videos, int Chapters, ImmutableDictionary<string, int> Variants)
    {
        public static Dictionary<string, Usage> Of(VideoRecord record)
        {
            var result = new Dictionary<string, Usage>(StringComparer.Ordinal);
            Count(record.Tags, video: true);
            foreach (var chapter in record.Chapters) Count(chapter.Tags, video: false);
            return result;

            void Count(IEnumerable<string> tags, bool video)
            {
                foreach (var group in tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).GroupBy(Key))
                {
                    var spelling = group.First().Trim();
                    var current = result.GetValueOrDefault(group.Key) ?? new Usage(0, 0, ImmutableDictionary<string, int>.Empty);
                    result[group.Key] = new Usage(current.Videos + (video ? 1 : 0), current.Chapters + (video ? 0 : 1),
                        current.Variants.SetItem(spelling, current.Variants.GetValueOrDefault(spelling) + 1));
                }
            }
        }

        public static bool SameCounts(Dictionary<string, Usage> left, Dictionary<string, Usage> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && other.Videos == pair.Value.Videos &&
                other.Chapters == pair.Value.Chapters && other.Variants.Count == pair.Value.Variants.Count &&
                other.Variants.All(variant => pair.Value.Variants.GetValueOrDefault(variant.Key) == variant.Value));
    }

    private sealed record Entry(int Videos, int Chapters, ImmutableDictionary<string, int> Variants, DateTimeOffset LastUsedUtc)
    {
        public int Uses => Videos + Chapters;

        public static Entry Create(Usage usage, DateTimeOffset usedAt) => new(usage.Videos, usage.Chapters, usage.Variants, usedAt);

        public Entry Add(Usage usage, DateTimeOffset usedAt) => new(Videos + usage.Videos, Chapters + usage.Chapters,
            usage.Variants.Aggregate(Variants, (variants, pair) => variants.SetItem(pair.Key, variants.GetValueOrDefault(pair.Key) + pair.Value)),
            usedAt > LastUsedUtc ? usedAt : LastUsedUtc);

        public Entry Subtract(Usage usage) => this with
        {
            Videos = Videos - usage.Videos,
            Chapters = Chapters - usage.Chapters,
            Variants = usage.Variants.Aggregate(Variants, (variants, pair) =>
                variants.GetValueOrDefault(pair.Key) - pair.Value is var left and > 0 ? variants.SetItem(pair.Key, left) : variants.Remove(pair.Key)),
        };

        public TagSuggestion ToSuggestion() => new(Variants.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key).FirstOrDefault() ?? "", Videos, Chapters, LastUsedUtc);
    }
}
