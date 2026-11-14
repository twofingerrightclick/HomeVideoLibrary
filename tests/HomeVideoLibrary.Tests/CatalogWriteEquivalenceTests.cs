using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class CatalogWriteEquivalenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-write-equivalence-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchFingerprintConflictKeepsSqliteExceptionAndRollsBackEarlierInserts(bool durable)
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = Video("first") with { SourceFingerprint = "v2:same-source" };
        var second = Video("second") with { SourceFingerprint = first.SourceFingerprint };

        var exception = await Assert.ThrowsAsync<SqliteException>(() => durable
            ? catalog.AddBatchDurablyAsync([first, second]) : catalog.AddBatchAsync([first, second]));

        Assert.True(LibraryCatalog.IsDuplicateSourceFingerprint(exception));
        Assert.Empty(await catalog.ListAsync());
        await catalog.AddAsync(first);
        Assert.Equal(first.Id, Assert.Single(await catalog.ListAsync()).Id);
    }

    [Fact]
    public async Task RepeatedBatchIdReachesSqliteAndRollsBack()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = Video("repeated");
        await Assert.ThrowsAsync<SqliteException>(() => catalog.AddBatchAsync([video, video]));
        Assert.Empty(await catalog.ListAsync());
    }

    [Fact]
    public async Task CacheUpsertSkipsMissingVideosAndReplacesUnreadableValuesWithoutReadingThem()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        await catalog.WriteCacheAsync([new(video.Id, new("old", 1, 2))]);
        await ExecuteAsync(catalog, "UPDATE video_cache SET file_length='unreadable'; UPDATE videos SET duration_ticks='unreadable'");
        var latest = new CatalogCacheEntry("latest", 30, 40);

        await catalog.WriteCacheAsync([
            new(Guid.NewGuid(), new("missing", 3, 4), TimeSpan.FromSeconds(99)),
            new(video.Id, new("intermediate", 10, 20), TimeSpan.FromSeconds(20)),
            new(video.Id, latest, TimeSpan.FromSeconds(30)),
        ]);

        var contents = await catalog.LoadContentsAsync();
        Assert.Empty(contents.Issues);
        Assert.Equal(latest, Assert.Single(contents.Cache).Value);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(contents.Videos).Duration);
    }

    [Fact]
    public async Task CacheFailureRollsBackPriorUpsertAndDurationTogether()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "first");
        var second = await AddVideoAsync(catalog, "second");
        var original = new CatalogCacheEntry("original", 1, 2);
        await catalog.WriteCacheAsync([new(first.Id, original)]);
        await ExecuteAsync(catalog, $"CREATE TRIGGER reject_second BEFORE INSERT ON video_cache WHEN NEW.video_id='{second.Id}' BEGIN SELECT RAISE(ABORT, 'test failure'); END");

        await Assert.ThrowsAsync<SqliteException>(() => catalog.WriteCacheAsync([
            new(first.Id, new("changed", 3, 4), TimeSpan.FromMinutes(1)),
            new(second.Id, new("rejected", 5, 6)),
        ]));

        var contents = await catalog.LoadContentsAsync();
        Assert.Equal(original, Assert.Single(contents.Cache).Value);
        Assert.All(contents.Videos, video => Assert.Equal(TimeSpan.FromSeconds(10), video.Duration));
    }

    [Fact]
    public async Task HistoryUpsertReplacesRevisionEvenWhenVideoIsAlreadyGoneAndCascadesRemoveDependents()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        var album = await new AlbumRepository(catalog).CreateAsync("Album", [video.Id]);
        await catalog.WriteCacheAsync([new(video.Id, new("cache", 1, 2))]);
        var history = new DeletionHistory("v2:deleted", "First title", DateTimeOffset.UtcNow, DeletionKind.Trashed);
        await catalog.DeleteVideosAsync([video.Id], deletion: history);
        var replacement = history with { Title = "Latest title", Kind = DeletionKind.PermanentlyDeleted, Revision = Guid.NewGuid() };
        await catalog.DeleteVideosAsync([video.Id], deletion: replacement);

        Assert.Equal(replacement, await catalog.FindDeletionAsync(history.Fingerprint));
        var contents = await catalog.LoadContentsAsync();
        Assert.Empty(contents.Videos);
        Assert.Empty(contents.Memberships);
        Assert.Empty(contents.Cache);
        Assert.Equal(album, Assert.Single(contents.Albums));
        Assert.True(File.Exists(catalog.GetVideoPath(video.RelativePath)));
    }

    [Fact]
    public async Task MembershipFailureRollsBackEarlierAddsAndKeepsFriendlyErrors()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "first");
        var second = await AddVideoAsync(catalog, "second");
        var albums = new AlbumRepository(catalog);
        var album = await albums.CreateAsync("Album", [first.Id, first.Id]);
        await albums.AddVideosAsync(album.Id, [first.Id]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => albums.AddVideosAsync(album.Id, [second.Id, Guid.NewGuid()]));

        Assert.Contains("no longer in the library", error.Message);
        Assert.Equal(second.Id, Assert.Single(await albums.ListNonMembersAsync(album.Id)).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => albums.CreateAsync("Rejected", [first.Id, Guid.NewGuid()]));
        Assert.Equal(album, Assert.Single(await albums.ListAsync()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => albums.CreateAsync("ALBUM"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => albums.RenameAsync(Guid.NewGuid(), "Missing"));
    }

    [Fact]
    public async Task FingerprintReleaseIsConditionalAndDoesNotReadMalformedVideoFields()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = Video("source") with { SourceFingerprint = "v2:original" };
        await catalog.AddAsync(video);
        await ExecuteAsync(catalog, "UPDATE videos SET duration_ticks='unreadable'");
        await catalog.ReleaseSourceFingerprintAsync(video.Id, "v2:wrong");
        var conflict = await Assert.ThrowsAsync<SqliteException>(() => catalog.AddAsync(Video("other") with { SourceFingerprint = "v2:original" }));
        Assert.True(LibraryCatalog.IsDuplicateSourceFingerprint(conflict));
        await catalog.ReleaseSourceFingerprintAsync(video.Id, "v2:original");
        await catalog.AddAsync(Video("replacement") with { SourceFingerprint = "v2:original" });
    }

    private static LibraryVideo Video(string name)
    {
        var id = Guid.NewGuid();
        return new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), name, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private static async Task ExecuteAsync(LibraryCatalog catalog, string sql)
    {
        await using var connection = await catalog.OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
