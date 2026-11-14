using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class CatalogReadEfEquivalenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-ef-reads-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task VideoReadsNormalizePathsAndSortParsedInstantsThenGuids()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "first", DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var second = await AddVideoAsync(catalog, "second", first.RecordedAt);
        var newest = await AddVideoAsync(catalog, "newest", first.RecordedAt.AddMinutes(1));
        await ExecuteAsync(catalog, """
            UPDATE videos SET relative_path=replace(relative_path,'/','\'), recorded_at='2023-12-31T19:01:00-05:00' WHERE id=$id
            """, newest.Id.ToString());

        var videos = await catalog.ListAsync();
        Assert.Equal(new[] { newest.Id }.Concat(new[] { first.Id, second.Id }.Order()), videos.Select(video => video.Id));
        Assert.Equal(newest.RelativePath, videos[0].RelativePath);
        Assert.Equal(newest.RelativePath, await catalog.GetRelativePathAsync(newest.Id));
        Assert.Contains(newest.RelativePath, await catalog.ListRelativePathsAsync());
        Assert.Null(await catalog.GetRelativePathAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SnapshotSkipsEachInvalidDomainRowAndKeepsOtherCollections()
    {
        var catalog = await CreateCatalogAsync(_root);
        var good = await AddVideoAsync(catalog, "good");
        var bad = await AddVideoAsync(catalog, "bad");
        var album = await new AlbumRepository(catalog).CreateAsync("Family", [good.Id]);
        await catalog.WriteCacheAsync([new(good.Id, new("{}", 13, 42)), new(bad.Id, new("ignored", 14, 43))]);
        await ExecuteAsync(catalog, "UPDATE videos SET recorded_at='invalid' WHERE id=$id", bad.Id.ToString());
        await ExecuteAsync(catalog, """
            INSERT INTO albums(id,name,name_key) VALUES('bad-album','Broken','BROKEN');
            INSERT INTO album_videos(album_id,video_id) VALUES('bad-album',$id);
            """, good.Id.ToString());

        var contents = await catalog.LoadContentsAsync();
        Assert.Equal(good.Id, Assert.Single(contents.Videos).Id);
        Assert.Equal(album.Id, Assert.Single(contents.Albums).Id);
        Assert.Equal((album.Id, good.Id), Assert.Single(contents.Memberships));
        Assert.Equal(new CatalogCacheEntry("{}", 13, 42), Assert.Single(contents.Cache).Value);
        Assert.Equal(3, contents.Issues.Count);
        Assert.Contains(contents.Issues, issue => issue.Contains("unreadable video entry") && issue.Contains(bad.Id.ToString()));
        Assert.Contains(contents.Issues, issue => issue.Contains("unreadable album entry (bad-album)"));
        Assert.Contains(contents.Issues, issue => issue.Contains("unreadable album membership entry (bad-album)"));
        Assert.True(await catalog.ContainsVideoAsync(bad.Id));
        Assert.Equal(bad.RelativePath, await catalog.GetRelativePathAsync(bad.Id));
        await Assert.ThrowsAsync<FormatException>(() => new AlbumRepository(catalog).ListAsync());
    }

    [Fact]
    public async Task AlbumQueriesPreserveTheirDistinctSqlOrdersAndNonMemberFiltering()
    {
        var catalog = await CreateCatalogAsync(_root);
        var repository = new AlbumRepository(catalog);
        var included = await AddVideoAsync(catalog, "included");
        var excluded = await AddVideoAsync(catalog, "excluded");
        var zulu = await repository.CreateAsync("Zulu", [included.Id]);
        var alpha = await repository.CreateAsync("alpha");
        await ExecuteAsync(catalog, "UPDATE albums SET name_key='000' WHERE id=$id", zulu.Id.ToString());

        Assert.Equal(new[] { zulu.Id, alpha.Id }, (await repository.ListAsync()).Select(album => album.Id));
        Assert.Equal(new[] { alpha.Id, zulu.Id }, (await catalog.LoadContentsAsync()).Albums.Select(album => album.Id));
        Assert.Equal(excluded.Id, Assert.Single(await repository.ListNonMembersAsync(zulu.Id)).Id);
        Assert.Equal((await catalog.ListAsync()).Select(video => video.Id),
            (await repository.ListNonMembersAsync(Guid.NewGuid())).Select(video => video.Id));
    }

    [Fact]
    public async Task FingerprintLookupRemainsCaseSensitiveAndParsesOrRejectsStoredValues()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        await ExecuteAsync(catalog, "UPDATE videos SET source_fingerprint='AbC' WHERE id=$id", video.Id.ToString());
        Assert.Null(await catalog.FindBySourceFingerprintAsync("abc"));
        Assert.Equal(video.Id, (await catalog.FindBySourceFingerprintAsync("AbC"))!.Id);
        await ExecuteAsync(catalog, "UPDATE videos SET relative_path='../outside.mkv' WHERE id=$id", video.Id.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.FindBySourceFingerprintAsync("AbC"));
        Assert.Null(await catalog.FindDeletionAsync("missing"));
        var revision = Guid.NewGuid();
        await ExecuteAsync(catalog, """
            INSERT INTO deletion_history(fingerprint,title,removed_at,kind,revision)
            VALUES('AbC','Family','2024-01-02T03:04:05+02:00',1,$id)
            """, revision.ToString());
        var deletion = await catalog.FindDeletionAsync("AbC");
        Assert.NotNull(deletion);
        Assert.Equal(revision, deletion.Revision);
        Assert.Equal(DateTimeOffset.Parse("2024-01-02T03:04:05+02:00"), deletion.RemovedAt);
        Assert.Null(await catalog.FindDeletionAsync("abc"));
    }

    [Fact]
    public async Task UnexpectedSqliteNumericStorageMatchesTheOriginalReader()
    {
        var catalog = await CreateCatalogAsync(_root);
        foreach (var value in new[] { "'not-an-integer'", "X'0102'", "9.9e99" })
        {
            var video = await AddVideoAsync(catalog, "numeric storage");
            await ExecuteAsync(catalog, $"UPDATE videos SET duration_ticks={value} WHERE id=$id", video.Id.ToString());
        }
        // SQLite's affinity/conversion rules, rather than CLR casts or parsing, define the old read behavior.
        var expected = new Dictionary<Guid, long>();
        var skipped = 0;
        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,duration_ticks FROM videos";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                try { expected.Add(Guid.Parse(reader.GetString(0)), reader.GetInt64(1)); }
                catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidCastException or OverflowException)
                {
                    skipped++;
                }
            }
        }
        var contents = await catalog.LoadContentsAsync();
        Assert.Equal(skipped, contents.Issues.Count);
        Assert.Equal(expected.Count, contents.Videos.Count);
        foreach (var video in contents.Videos) Assert.Equal(expected[video.Id], video.Duration.Ticks);
        Assert.Equal(expected.Keys.Order(), (await catalog.ListAsync()).Select(video => video.Id).Order());
    }

    [Fact]
    public async Task ScalarReadsRejectBlobValuesInsteadOfCoercingThemToText()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        await catalog.SaveNameAsync("Family");
        await ExecuteAsync(catalog, "UPDATE library_info SET name=CAST('Family' AS BLOB)");
        await ExecuteAsync(catalog, "UPDATE videos SET relative_path=CAST(relative_path AS BLOB) WHERE id=$id", video.Id.ToString());
        Assert.Equal(LibraryCatalog.DefaultName, await catalog.ReadNameAsync());
        Assert.Null(await catalog.GetRelativePathAsync(video.Id));
        Assert.False(await catalog.ContainsVideoAsync(video.Id));
        // The original row reader uses GetString, which does coerce a BLOB to text.
        Assert.Equal(video.RelativePath, Assert.Single(await catalog.ListAsync()).RelativePath);
        Assert.Contains(video.RelativePath, await catalog.ListRelativePathsAsync());
    }

    private static async Task ExecuteAsync(LibraryCatalog catalog, string sql, string? id = null)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalog.Paths.DatabasePath, Pooling = false, ForeignKeys = true,
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => DeleteDirectory(_root);
}
