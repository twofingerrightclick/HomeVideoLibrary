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
        long version;
        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version";
            version = Convert.ToInt64(await versionCommand.ExecuteScalarAsync(cancellationToken));
            if (version > 3)
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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (version < 3)
        {
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('videos') WHERE name = 'source_fingerprint'";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                command.CommandText = "ALTER TABLE videos ADD COLUMN source_fingerprint TEXT NULL";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS videos_source_fingerprint ON videos(source_fingerprint);
            PRAGMA user_version = 3;
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
        command.CommandText = $"SELECT {VideoColumns} FROM videos ORDER BY recorded_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var videos = new List<LibraryVideo>();
        while (await reader.ReadAsync(cancellationToken))
            videos.Add(ReadVideo(reader));
        return videos;
    }

    public async Task<LibraryVideo?> FindBySourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {VideoColumns} FROM videos WHERE source_fingerprint = $fingerprint LIMIT 1";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadVideo(reader) : null;
    }

    private const string VideoColumns = "id,relative_path,display_name,duration_ticks,imported_at,recorded_at,source_fingerprint";

    private LibraryVideo ReadVideo(SqliteDataReader reader)
    {
        var relativePath = reader.GetString(1);
        _ = GetVideoPath(relativePath);
        return new LibraryVideo(Guid.Parse(reader.GetString(0)), relativePath, reader.GetString(2),
            TimeSpan.FromTicks(reader.GetInt64(3)), DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture))
        {
            SourceFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
        };
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
            command.CommandText = $"INSERT INTO videos ({VideoColumns}) VALUES ($id,$path,$name,$duration,$imported,$recorded,$fingerprint)";
            command.Parameters.AddWithValue("$id", video.Id.ToString()); command.Parameters.AddWithValue("$path", video.RelativePath);
            command.Parameters.AddWithValue("$name", video.DisplayName); command.Parameters.AddWithValue("$duration", video.Duration.Ticks);
            command.Parameters.AddWithValue("$imported", video.ImportedAt.ToString("O")); command.Parameters.AddWithValue("$recorded", video.RecordedAt.ToString("O"));
            command.Parameters.AddWithValue("$fingerprint", (object?)video.SourceFingerprint ?? DBNull.Value);
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
