using System.Collections.Immutable;
using System.Diagnostics;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class AlbumsAndSearchTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-search-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AlbumMembershipIsIdempotentNeverDeletesMediaAndReportsFriendlyErrors()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "first"); var second = await AddVideoAsync(catalog, "second");
        var albums = new AlbumRepository(catalog);
        var album = await albums.CreateAsync(" Family ");
        await albums.AddVideosAsync(album.Id, [first.Id, first.Id]);
        await albums.AddVideosAsync(album.Id, [first.Id]);
        Assert.Equal(second.Id, Assert.Single(await albums.ListNonMembersAsync(album.Id)).Id);
        await albums.RenameAsync(album.Id, "Family films");
        Assert.Equal("Family films", Assert.Single(await albums.ListAsync()).Name);
        await albums.RemoveVideosAsync(album.Id, [first.Id]);
        await albums.RemoveVideosAsync(album.Id, [first.Id]);
        Assert.Equal(2, (await albums.ListNonMembersAsync(album.Id)).Count);
        await albums.AddVideosAsync(album.Id, [first.Id]);
        await albums.DeleteAsync(album.Id);
        Assert.Empty(await albums.ListAsync());
        Assert.Equal(2, (await catalog.ListAsync()).Count);
        Assert.Equal("source first", await File.ReadAllTextAsync(catalog.GetVideoPath(first.RelativePath)));
        Assert.Equal("source second", await File.ReadAllTextAsync(catalog.GetVideoPath(second.RelativePath)));

        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => albums.AddVideosAsync(album.Id, [first.Id]));
        Assert.Contains("no longer in the library", gone.Message);
        var fromSelection = await albums.CreateAsync("From selection", [first.Id, second.Id, first.Id]);
        Assert.Empty(await albums.ListNonMembersAsync(fromSelection.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => albums.CreateAsync("Invalid selection", [Guid.NewGuid()]));
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => albums.CreateAsync("FROM SELECTION"));
        Assert.Contains("already exists", duplicate.Message);
        var other = await albums.CreateAsync("Other");
        await Assert.ThrowsAsync<InvalidOperationException>(() => albums.RenameAsync(other.Id, "from selection"));
        Assert.Equal(["From selection", "Other"], (await albums.ListAsync()).Select(item => item.Name));
    }

    [Fact]
    public void SearchMatchesWordPrefixesInEveryFieldIgnoringCaseAndDiacritics()
    {
        var chapter = new VideoChapterRecord(7, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), "Candles",
            "Grandmother blows the birthday candles", ["cake-time"]);
        var video = Record("filename", title: "Été à Montréal", description: "祖母生日 family holiday", tags: ["réunion"], chapters: [chapter]);
        var other = Record("other", title: "Beach day", tags: ["summer"]);
        var snapshot = Snapshot([video, other]);

        foreach (var query in new[] { "été", "ETE mont", "réun", "REUNION", "Grandmother", "cake", "file", "祖母", "生日" })
            Assert.Equal(video.Id, Assert.Single(LibrarySearch.Run(snapshot, query)).Video.Id);
        Assert.Empty(LibrarySearch.Run(snapshot, "ete beach"));
        Assert.Equal(chapter, Assert.Single(LibrarySearch.Run(snapshot, "grandmother")).Chapter);
        Assert.Null(Assert.Single(LibrarySearch.Run(snapshot, "montreal")).Chapter);
        Assert.Equal(video.Id, Assert.Single(LibrarySearch.Run(snapshot, "\"birthday candles\"")).Video.Id);
        Assert.Empty(LibrarySearch.Run(snapshot, "\"candles birthday\""));
        foreach (var query in new[] { "\"", "() * : NEAR(", "' OR 1=1; DROP TABLE videos; --", "́", "tag:" })
            _ = LibrarySearch.Run(snapshot, query); // Punctuation and operators are plain text.
    }

    [Fact]
    public void SubstringsAreOnlyAFallbackWhenNoVideoMatchesByPrefix()
    {
        var party = Record("party", title: "Garden party");
        var art = Record("art", title: "Art class");
        var snapshot = Snapshot([party, art]);

        Assert.Equal(art.Id, Assert.Single(LibrarySearch.Run(snapshot, "art")).Video.Id);
        Assert.Equal(party.Id, Assert.Single(LibrarySearch.Run(snapshot, "arty")).Video.Id);
    }

    [Fact]
    public void TagFilterMatchesVideoAndChapterTagsAndPointsAtTheChapter()
    {
        var chapter = new VideoChapterRecord(1, TimeSpan.Zero, TimeSpan.FromSeconds(5), "Opening", "", ["Birthday party"]);
        var chapterTagged = Record("chapter tagged", chapters: [chapter]);
        var videoTagged = Record("video tagged", tags: ["birthday"]);
        var mentionsWord = Record("birthday in the title");
        var snapshot = Snapshot([chapterTagged, videoTagged, mentionsWord]);

        var hits = LibrarySearch.Run(snapshot, "tag:birth");
        Assert.Equal(new[] { chapterTagged.Id, videoTagged.Id }.Order(), hits.Select(hit => hit.Video.Id).Order());
        Assert.Equal(chapter, hits.Single(hit => hit.Video.Id == chapterTagged.Id).Chapter);
        Assert.Equal(chapterTagged.Id, Assert.Single(LibrarySearch.Run(snapshot, "tag:\"birthday party\"")).Video.Id);
        Assert.Empty(LibrarySearch.Run(snapshot, "tag:birthday title"));
    }

    [Fact]
    public void TagFilterUsesTheSuggestionPrefixRulePhrasesStayInOneFieldAndPunctuationFiltersNothing()
    {
        var family = Record("family", title: "end", description: "start", tags: ["my family"]);
        var other = Record("other");
        var snapshot = Snapshot([family, other]);

        Assert.Equal(family.Id, Assert.Single(LibrarySearch.Run(snapshot, "tag:fam")).Video.Id);
        Assert.Equal(family.Id, Assert.Single(LibrarySearch.Run(snapshot, "tag:my")).Video.Id);
        Assert.Empty(LibrarySearch.Run(snapshot, "tag:ily"));
        Assert.Empty(LibrarySearch.Run(snapshot, "\"end start\""));
        Assert.Equal(2, LibrarySearch.Run(snapshot, "!!!").Count);
        Assert.Equal(2, LibrarySearch.Run(snapshot, "#").Count);
    }

    [Fact]
    public void AlbumFiltersAndPendingRecordsAndNewestFirstOrder()
    {
        var older = Record("older trip", recordedAt: new DateTimeOffset(1995, 1, 1, 0, 0, 0, TimeSpan.Zero), title: "trip");
        var newer = Record("newer trip", recordedAt: new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero), title: "trip");
        // Embedded dates win over the catalog date, and compare as instants across offsets.
        var embedded = Record("embedded trip", recordedAt: new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero), title: "trip")
            with { EmbeddedRecordedAt = new DateTimeOffset(1997, 6, 1, 1, 0, 0, TimeSpan.FromHours(5)) };
        var pending = Record("Unread holiday tape", state: VideoRecordState.Pending);
        var albumId = Guid.NewGuid();
        var snapshot = Snapshot([older, newer, embedded, pending], (albumId, [older.Id, embedded.Id]));

        Assert.Equal([pending.Id, newer.Id, embedded.Id, older.Id], LibrarySearch.Run(snapshot, "").Select(hit => hit.Video.Id));
        Assert.Equal([embedded.Id, older.Id], LibrarySearch.Run(snapshot, "trip", AlbumFilter.Members(albumId)).Select(hit => hit.Video.Id));
        Assert.Equal([pending.Id, newer.Id], LibrarySearch.Run(snapshot, null, AlbumFilter.NonMembers(albumId)).Select(hit => hit.Video.Id));
        Assert.Equal(pending.Id, Assert.Single(LibrarySearch.Run(snapshot, "holiday")).Video.Id);
    }

    [Fact]
    public void KeystrokeSearchOverTenThousandRecordsIsFast()
    {
        var words = new[] { "beach", "birthday", "grandma", "christmas", "garden", "school", "concert", "picnic", "wedding", "puppy" };
        var records = Enumerable.Range(0, 10_000).Select(index => Record($"tape {index}",
            title: $"{words[index % 10]} {words[(index / 10) % 10]} {index}", description: $"Recorded at home with the family, take {index}",
            tags: [words[(index / 7) % 10], "family"],
            chapters: [new VideoChapterRecord(1, TimeSpan.Zero, TimeSpan.FromSeconds(10), $"{words[(index / 3) % 10]} moment", "", [])])).ToArray();
        var snapshot = Snapshot(records);
        _ = LibrarySearch.Run(snapshot, "warm up");

        var timer = Stopwatch.StartNew();
        var hits = 0;
        foreach (var query in new[] { "b", "bi", "bir", "birt", "birth", "birthday", "birthday g", "birthday ga", "birthday garden" })
            hits = LibrarySearch.Run(snapshot, query).Count;
        timer.Stop();
        var perKeystroke = timer.Elapsed.TotalMilliseconds / 9;
        output.WriteLine($"{perKeystroke:0.00} ms per keystroke over {records.Length} records ({hits} final hits)");
        Assert.True(hits > 0);
        Assert.True(perKeystroke < 50, $"Search took {perKeystroke:0.0} ms per keystroke.");
    }

    private static LibrarySnapshot Snapshot(IReadOnlyList<VideoRecord> records, params (Guid Album, Guid[] Members)[] albums) =>
        new(records.ToImmutableDictionary(record => record.Id),
            [.. albums.Select(album => new VideoAlbum(album.Album, "Album"))],
            albums.ToImmutableDictionary(album => album.Album, album => album.Members.ToImmutableHashSet()),
            TagIndex.Build(records));

    public void Dispose() => DeleteDirectory(_root);
}
