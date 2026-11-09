using HomeVideoLibrary.Core;
using Microsoft.Data.Sqlite;

namespace HomeVideoLibrary.Infrastructure;

public sealed class LibraryCatalog(LibraryPaths paths)
{
    public LibraryPaths Paths { get; } = paths;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        Directory.CreateDirectory(Paths.VideosDirectory);
        Directory.CreateDirectory(Paths.CacheDirectory);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt64(await version.ExecuteScalarAsync(cancellationToken)) > 2)
                throw new InvalidDataException("This library was created by a newer version of Home Video Library.");
        }
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
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
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public string GetVideoPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("The catalog requires a relative video path.", nameof(relativePath));
        var fullPath = Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
        var relative = Path.GetRelativePath(Paths.VideosDirectory, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("The video path must be inside the library's Videos directory.", nameof(relativePath));
        return fullPath;
    }

    public async Task<IReadOnlyList<LibraryVideo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,relative_path,display_name,duration_ticks,imported_at,recorded_at FROM videos ORDER BY recorded_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var videos = new List<LibraryVideo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var relativePath = reader.GetString(1);
            _ = GetVideoPath(relativePath);
            videos.Add(new LibraryVideo(Guid.Parse(reader.GetString(0)), relativePath, reader.GetString(2),
                TimeSpan.FromTicks(reader.GetInt64(3)), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture)));
        }
        return videos;
    }

    public Task AddAsync(LibraryVideo video, CancellationToken cancellationToken = default) => AddBatchAsync([video], cancellationToken);

    public async Task AddBatchAsync(IReadOnlyList<LibraryVideo> videos, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var video in videos)
        {
            _ = GetVideoPath(video.RelativePath);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO videos VALUES ($id,$path,$name,$duration,$imported,$recorded)";
            command.Parameters.AddWithValue("$id", video.Id.ToString()); command.Parameters.AddWithValue("$path", video.RelativePath);
            command.Parameters.AddWithValue("$name", video.DisplayName); command.Parameters.AddWithValue("$duration", video.Duration.Ticks);
            command.Parameters.AddWithValue("$imported", video.ImportedAt.ToString("O")); command.Parameters.AddWithValue("$recorded", video.RecordedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.DatabasePath, Pooling = false, ForeignKeys = true }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
