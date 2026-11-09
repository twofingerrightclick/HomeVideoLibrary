using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Restricts a search to an album's members, or to the videos outside it.</summary>
public readonly record struct AlbumFilter(Guid? AlbumId, bool ExcludeMembers)
{
    public static AlbumFilter All => default;
    public static AlbumFilter Members(Guid albumId) => new(albumId, false);
    public static AlbumFilter NonMembers(Guid albumId) => new(albumId, true);
}

/// <param name="Chapter">The first chapter whose own text or tags matched, for jumping straight to it.</param>
public sealed record SearchHit(VideoRecord Video, VideoChapterRecord? Chapter = null);

/// <summary>In-memory search over a <see cref="LibrarySnapshot"/>.</summary>
/// <remarks>Query syntax: words match case- and diacritic-insensitively as word prefixes across the display name, title,
/// description, tags and chapter text, and all words must match. <c>"a phrase"</c> matches as a substring, and
/// <c>tag:name</c> or <c>tag:"two words"</c> requires a tag (video or chapter) starting with that name. When no
/// video matches every word as a prefix, words are retried as substrings. Results are newest first.</remarks>
public static partial class LibrarySearch
{
    private const int MaxTokens = 32;
    private static readonly ConditionalWeakTable<VideoRecord, Document> Documents = [];

    public static IReadOnlyList<SearchHit> Run(LibrarySnapshot snapshot, string? query, AlbumFilter filter = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = Filter(snapshot, filter);
        if (string.IsNullOrWhiteSpace(query)) return candidates.Select(record => new SearchHit(record)).ToArray();
        var parsed = Parse(query);
        if (parsed.IsEmpty) return [];
        var hits = Match(candidates, parsed, substrings: false);
        return hits.Count > 0 || parsed.Words.Length == 0 ? hits : Match(candidates, parsed, substrings: true);
    }

    /// <summary>Lowercases and strips diacritics and compatibility forms, so "Été" and "ete" compare equal.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static IEnumerable<VideoRecord> Filter(LibrarySnapshot snapshot, AlbumFilter filter)
    {
        if (filter.AlbumId is not { } albumId) return snapshot.Chronological;
        var members = snapshot.MembersOf(albumId);
        return snapshot.Chronological.Where(record => members.Contains(record.Id) != filter.ExcludeMembers);
    }

    private static List<SearchHit> Match(IEnumerable<VideoRecord> candidates, Query query, bool substrings)
    {
        var hits = new List<SearchHit>();
        foreach (var record in candidates)
        {
            var document = Documents.GetValue(record, static record => new Document(record));
            if (!query.Tags.All(document.HasTag) ||
                !query.Words.All(word => document.Video.Matches(word, substrings) || document.Chapters.Any(chapter => chapter.Matches(word, substrings))) ||
                !query.Phrases.All(phrase => document.Video.Contains(phrase) || document.Chapters.Any(chapter => chapter.Contains(phrase))))
                continue;
            var chapter = document.Chapters.FirstOrDefault(chapter =>
                query.Words.Any(word => chapter.Matches(word, substrings)) || query.Phrases.Any(chapter.Contains) ||
                query.Tags.Any(tag => !document.Video.HasTag(tag) && chapter.HasTag(tag)));
            hits.Add(new SearchHit(record, chapter?.Chapter));
        }
        return hits;
    }

    private static Query Parse(string query)
    {
        var words = new List<string>();
        var phrases = new List<string>();
        var tags = new List<string>();
        foreach (Match match in QueryRegex().Matches(query))
        {
            if (match.Groups["tag"].Success) Add(tags, TagIndex.Key(match.Groups["tag"].Value));
            else if (match.Groups["phrase"].Success)
            {
                var phrase = Fold(match.Groups["phrase"].Value).Trim();
                if (WordRegex().IsMatch(phrase)) Add(phrases, phrase);
            }
            else foreach (Match word in WordRegex().Matches(Fold(match.Value))) Add(words, word.Value);
        }
        return new Query([.. words], [.. phrases], [.. tags]);

        void Add(List<string> list, string value)
        {
            if (value.Length > 0 && words.Count + phrases.Count + tags.Count < MaxTokens && !list.Contains(value)) list.Add(value);
        }
    }

    private sealed record Query(string[] Words, string[] Phrases, string[] Tags)
    {
        public bool IsEmpty => Words.Length == 0 && Phrases.Length == 0 && Tags.Length == 0;
    }

    private sealed class Document
    {
        public Document(VideoRecord record)
        {
            Video = new Text(string.Join('\n', [record.DisplayName, record.Title, record.Description, .. record.Tags]), record.Tags);
            Chapters = record.Chapters.Select(chapter => new ChapterText(chapter)).ToArray();
        }

        public Text Video { get; }
        public ChapterText[] Chapters { get; }
        public bool HasTag(string prefix) => Video.HasTag(prefix) || Chapters.Any(chapter => chapter.HasTag(prefix));
    }

    private class Text
    {
        private readonly string _text;
        private readonly string[] _words;
        private readonly string[] _tags;

        public Text(string text, IEnumerable<string> tags)
        {
            _text = Fold(text);
            _words = WordRegex().Matches(_text).Select(match => match.Value).Distinct().ToArray();
            _tags = tags.Select(TagIndex.Key).Where(tag => tag.Length > 0).ToArray();
        }

        public bool Matches(string word, bool substring) => substring
            ? _text.Contains(word, StringComparison.Ordinal)
            : _words.Any(candidate => candidate.StartsWith(word, StringComparison.Ordinal));

        public bool Contains(string phrase) => _text.Contains(phrase, StringComparison.Ordinal);
        public bool HasTag(string prefix) => _tags.Any(tag => tag.StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class ChapterText(VideoChapterRecord chapter)
        : Text(string.Join('\n', [chapter.Title, chapter.Description, .. chapter.Tags]), chapter.Tags)
    {
        public VideoChapterRecord Chapter { get; } = chapter;
    }

    [GeneratedRegex("""tag:"(?<tag>[^"]*)"?|tag:(?<tag>\S+)|"(?<phrase>[^"]*)"?|[^\s"]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QueryRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
