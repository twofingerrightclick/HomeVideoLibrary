using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HomeVideoLibrary.Core;

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
/// description, tags, displayed recording year and chapter text, and all words must match. <c>"a phrase"</c> matches as a substring, and
/// <c>tag:name</c> or <c>tag:"two words"</c> requires a tag (video or chapter) matching by <see cref="TagRules.MatchesPrefix"/>,
/// the rule tag suggestions use. Phrases never span two fields. When no video matches every word as a prefix, words are
/// retried as substrings. A query without any searchable text filters nothing. Results are newest first.</remarks>
public static partial class LibrarySearch
{
    private const int MaxTokens = 32;
    private static readonly ConditionalWeakTable<VideoRecord, Document> Documents = [];

    public static IReadOnlyList<SearchHit> Run(LibrarySnapshot snapshot, string? query, AlbumFilter filter = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = Filter(snapshot, filter);
        var parsed = Parse(query ?? "");
        if (parsed.IsEmpty) return candidates.Select(record => new SearchHit(record)).ToArray();
        var hits = Match(candidates, parsed, substrings: false);
        return hits.Count > 0 || parsed.Words.Length == 0 ? hits : Match(candidates, parsed, substrings: true);
    }

    /// <summary>Builds the search text for records ahead of the first query. Runs where records are published (the model's
    /// writer, off the UI thread); records never prepared are built on first search instead.</summary>
    internal static void Prepare(IEnumerable<VideoRecord> records)
    {
        foreach (var record in records) _ = DocumentOf(record);
    }

    private static Document DocumentOf(VideoRecord record) => Documents.GetValue(record, static record => new Document(record));

    /// <summary>Text folds exactly like tag identity, so a word query and a <c>tag:</c> filter agree on "Été" = "ete".</summary>
    private static string Fold(string text) => TagRules.Key(text);

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
            var document = DocumentOf(record);
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
            if (match.Groups["tag"].Success) Add(tags, TagRules.Key(match.Groups["tag"].Value));
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
            Video = new Text([record.DisplayName, record.Title, record.Description,
                record.RecordedAt.ToLocalTime().Year.ToString(CultureInfo.InvariantCulture), .. record.Tags], record.Tags);
            Chapters = record.Chapters.Select(chapter => new ChapterText(chapter)).ToArray();
        }

        public Text Video { get; }
        public ChapterText[] Chapters { get; }
        public bool HasTag(string prefix) => Video.HasTag(prefix) || Chapters.Any(chapter => chapter.HasTag(prefix));
    }

    private class Text
    {
        private readonly string[] _fields;
        private readonly string[] _words;
        private readonly string[] _tags;

        public Text(IEnumerable<string> fields, IEnumerable<string> tags)
        {
            _fields = fields.Select(Fold).Where(field => field.Length > 0).ToArray();
            _words = _fields.SelectMany(field => WordRegex().Matches(field).Select(match => match.Value)).Distinct().ToArray();
            _tags = tags.Select(TagRules.Key).Where(tag => tag.Length > 0).ToArray();
        }

        public bool Matches(string word, bool substring) => substring
            ? Contains(word)
            : _words.Any(candidate => candidate.StartsWith(word, StringComparison.Ordinal));

        public bool Contains(string text) => _fields.Any(field => field.Contains(text, StringComparison.Ordinal));
        public bool HasTag(string prefix) => _tags.Any(tag => TagRules.MatchesPrefix(tag, prefix));
    }

    private sealed class ChapterText(VideoChapterRecord chapter)
        : Text([chapter.Title, chapter.Description, .. chapter.Tags], chapter.Tags)
    {
        public VideoChapterRecord Chapter { get; } = chapter;
    }

    [GeneratedRegex("""tag:"(?<tag>[^"]*)"?|tag:(?<tag>\S+)|"(?<phrase>[^"]*)"?|[^\s"]+""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QueryRegex();

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
