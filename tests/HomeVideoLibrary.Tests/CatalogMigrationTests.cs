using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class CatalogMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-migration-" + Guid.NewGuid().ToString("N"));

    /// <summary>The schema exactly as version 2 of the app (commit 9217966) created it.</summary>
    private const string VersionTwoSchema = """
        CREATE TABLE IF NOT EXISTS videos (
            id TEXT PRIMARY KEY, relative_path TEXT NOT NULL UNIQUE,
            display_name TEXT NOT NULL, duration_ticks INTEGER NOT NULL,
            imported_at TEXT NOT NULL, recorded_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS albums (id TEXT PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS album_videos (
            album_id TEXT NOT NULL REFERENCES albums(id) ON DELETE CASCADE,
            video_id TEXT NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
            PRIMARY KEY (album_id,video_id)
        );
        CREATE INDEX IF NOT EXISTS album_videos_video ON album_videos(video_id);
        CREATE TABLE IF NOT EXISTS search_cache (
            rowid INTEGER PRIMARY KEY, video_id TEXT NOT NULL UNIQUE REFERENCES videos(id) ON DELETE CASCADE,
            title TEXT NOT NULL, description TEXT NOT NULL, tags TEXT NOT NULL, chapters TEXT NOT NULL,
            recorded_at TEXT NOT NULL, file_length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS video_fts USING fts5(title,description,tags,chapters,
            content='search_cache',content_rowid='rowid',tokenize='unicode61 remove_diacritics 2');
        CREATE TRIGGER IF NOT EXISTS cache_insert AFTER INSERT ON search_cache BEGIN
            INSERT INTO video_fts(rowid,title,description,tags,chapters) VALUES(new.rowid,new.title,new.description,new.tags,new.chapters);
        END;
        CREATE TRIGGER IF NOT EXISTS cache_delete AFTER DELETE ON search_cache BEGIN
            INSERT INTO video_fts(video_fts,rowid,title,description,tags,chapters) VALUES('delete',old.rowid,old.title,old.description,old.tags,old.chapters);
        END;
        CREATE TRIGGER IF NOT EXISTS cache_update AFTER UPDATE ON search_cache BEGIN
            INSERT INTO video_fts(video_fts,rowid,title,description,tags,chapters) VALUES('delete',old.rowid,old.title,old.description,old.tags,old.chapters);
            INSERT INTO video_fts(rowid,title,description,tags,chapters) VALUES(new.rowid,new.title,new.description,new.tags,new.chapters);
        END;
        PRAGMA user_version = 2;
        """;

    [Fact]
    public async Task WindowsCreatedVersionTwoLibraryMigratesKeepingDataAndABackup()
    {
        var paths = new LibraryPaths(_root);
        Directory.CreateDirectory(paths.VideosDirectory);
        Guid windows = Guid.NewGuid(), unix = Guid.NewGuid(), family = Guid.NewGuid(), familyAgain = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(paths.VideosDirectory, $"{windows:N}.mkv"), "windows");
        await ExecuteAsync(paths, VersionTwoSchema + $"""
            INSERT INTO videos VALUES ('{windows}', 'Videos\{windows:N}.mkv', 'From Windows', 10000000, '2024-01-02T03:04:05.0000000+01:00', '1997-07-03T00:00:00.0000000-05:00');
            INSERT INTO videos VALUES ('{unix}', 'Videos/{unix:N}.mkv', 'From macOS', 20000000, '2024-01-02T03:04:05.0000000+00:00', '1998-01-01T00:00:00.0000000+00:00');
            INSERT INTO albums VALUES ('{family}', 'Family'), ('{familyAgain}', 'family');
            INSERT INTO album_videos VALUES ('{family}', '{windows}'), ('{familyAgain}', '{unix}');
            INSERT INTO search_cache(video_id,title,description,tags,chapters,recorded_at,file_length,modified_ticks)
                VALUES ('{windows}', 'Old title', '', '', '', '1997-07-03T05:00:00.0000000+00:00', 7, 1);
            """);

        var catalog = new LibraryCatalog(paths);
        await catalog.InitializeAsync();

        Assert.Equal(4L, await ScalarAsync(paths.DatabasePath, "PRAGMA user_version"));
        Assert.Equal("wal", await ScalarAsync(paths.DatabasePath, "PRAGMA journal_mode"));
        Assert.Equal(0L, await ScalarAsync(paths.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('search_cache','video_fts','cache_insert')"));
        Assert.Equal($"Videos/{windows:N}.mkv", await ScalarAsync(paths.DatabasePath, $"SELECT relative_path FROM videos WHERE id='{windows}'"));
        var contents = await catalog.LoadContentsAsync();
        Assert.Empty(contents.Issues);
        var migrated = contents.Videos.Single(video => video.Id == windows);
        Assert.Equal("windows", await File.ReadAllTextAsync(catalog.GetVideoPath(migrated.RelativePath)));
        Assert.Equal(new DateTimeOffset(1997, 7, 3, 5, 0, 0, TimeSpan.Zero), migrated.RecordedAt);
        Assert.Equal(["Family", "family (2)"], contents.Albums.Select(album => album.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, contents.Memberships.Count);
        Assert.Empty(contents.Cache);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AlbumRepository(catalog).CreateAsync("FAMILY"));

        var backup = Path.Combine(paths.CacheDirectory, "backup-v2.sqlite3");
        Assert.Equal("delete", await ScalarAsync(backup, "PRAGMA journal_mode"));
        Assert.Equal(2L, await ScalarAsync(backup, "PRAGMA user_version"));
        Assert.Equal(2L, await ScalarAsync(backup, "SELECT COUNT(*) FROM videos"));
        Assert.Equal(1L, await ScalarAsync(backup, "SELECT COUNT(*) FROM search_cache"));

        await catalog.InitializeAsync();
        Assert.Single(Directory.GetFiles(paths.CacheDirectory, "backup-v*.sqlite3"));
        var model = new LibraryModel(catalog, new FakeMatroska());
        await model.LoadAsync();
        Assert.Equal(2, model.Snapshot.Videos.Count);
        Assert.All(model.Snapshot.Videos.Values, record => Assert.Equal(VideoRecordState.Pending, record.State));
    }

    [Fact]
    public async Task MigratesVersionOneCatalogWithoutLosingItsVideos()
    {
        var paths = new LibraryPaths(_root); Directory.CreateDirectory(paths.RootDirectory);
        var id = Guid.NewGuid();
        await ExecuteAsync(paths, $"""
            CREATE TABLE videos(id TEXT PRIMARY KEY,relative_path TEXT UNIQUE NOT NULL,display_name TEXT NOT NULL,duration_ticks INTEGER NOT NULL,imported_at TEXT NOT NULL,recorded_at TEXT NOT NULL);
            INSERT INTO videos VALUES ('{id}','Videos/legacy.mkv','Legacy',10000000,'{DateTimeOffset.Now:O}','{DateTimeOffset.Now:O}');
            PRAGMA user_version=1;
            """);
        var catalog = new LibraryCatalog(paths);
        await catalog.InitializeAsync(); await catalog.InitializeAsync();
        Assert.Equal(id, Assert.Single(await catalog.ListAsync()).Id);
        Assert.NotNull(await new AlbumRepository(catalog).CreateAsync("Migrated album"));
        Assert.True(File.Exists(Path.Combine(paths.CacheDirectory, "backup-v1.sqlite3")));
    }

    [Fact]
    public async Task NewLibraryNeedsNoBackupAndANewerSchemaIsRefused()
    {
        var catalog = await CreateCatalogAsync(_root);
        Assert.Empty(Directory.GetFiles(catalog.Paths.CacheDirectory, "backup-*"));
        await ExecuteAsync(catalog.Paths, "PRAGMA user_version = 5");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LibraryCatalog(catalog.Paths).InitializeAsync());
    }

    [Fact]
    public async Task UnreadableRowsAreSkippedAndReported()
    {
        var catalog = await CreateCatalogAsync(_root);
        var good = await AddVideoAsync(catalog, "good");
        await ExecuteAsync(catalog.Paths, """
            INSERT INTO videos VALUES ('not-a-guid', 'Videos/bad.mkv', 'Bad', 1, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', NULL);
            INSERT INTO videos VALUES ('7b0a5f8e-35a4-4c43-9d43-0d3e3b0b4d11', '../outside.mkv', 'Escapes', 1, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z', NULL);
            """);

        Assert.Equal(good.Id, Assert.Single(await catalog.ListAsync()).Id);
        var contents = await catalog.LoadContentsAsync();
        Assert.Equal(good.Id, Assert.Single(contents.Videos).Id);
        Assert.Equal(2, contents.Issues.Count);
    }

    private static async Task ExecuteAsync(LibraryPaths paths, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False;Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public void Dispose() => DeleteDirectory(_root);
}
