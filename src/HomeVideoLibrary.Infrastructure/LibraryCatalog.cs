using System.Diagnostics;
using System.Globalization;
using HomeVideoLibrary.Core;
using Microsoft.Data.Sqlite;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>A cached MKV projection for one video, keyed to the file stamp it was read from.</summary>
public sealed record CatalogCacheEntry(string RecordJson, long FileLength, long ModifiedTicks);

/// <param name="Duration">When set, also replaces the catalog's duration for the video.</param>
public sealed record CatalogCacheWrite(Guid VideoId, CatalogCacheEntry Entry, TimeSpan? Duration = null);

/// <param name="Issues">Rows that could not be read and were skipped, as user-facing text.</param>
public sealed record CatalogContents(IReadOnlyList<LibraryVideo> Videos, IReadOnlyList<VideoAlbum> Albums,
    IReadOnlyList<(Guid AlbumId, Guid VideoId)> Memberships, IReadOnlyDictionary<Guid, CatalogCacheEntry> Cache,
    IReadOnlyList<string> Issues);

/// <param name="isNetworkVolume">Decides the journal mode; defaults to asking the OS about the library's volume.</param>
public sealed class LibraryCatalog(LibraryPaths paths, Func<string, bool>? isNetworkVolume = null)
{
    public const int SchemaVersion = 5;

    public LibraryPaths Paths { get; } = paths;

    /// <summary>Creates or migrates the database. Before migrating an existing database it is backed up to
    /// <c>.cache/backup-v{version}.sqlite3</c>; each migration step commits on its own, so a failure keeps every earlier
    /// step and loses no data.</summary>
    /// <exception cref="InvalidDataException">The library is from a newer version, or an upgrade step failed.</exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Paths.RootDirectory);
        Directory.CreateDirectory(Paths.VideosDirectory);
        Directory.CreateDirectory(Paths.CacheDirectory);
        await using var connection = await OpenAsync(cancellationToken);
        var version = await ScalarAsync(connection, null, "PRAGMA user_version", cancellationToken);
        if (version > SchemaVersion)
            throw new InvalidDataException("This library was created by a newer version of Home Video Library.");
        var backup = version < SchemaVersion && await ScalarAsync(connection, null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='videos'", cancellationToken) > 0
            ? BackUp(connection, version) : null;
        await SetJournalModeAsync(connection, cancellationToken);
        if (version == SchemaVersion) return;
        try
        {
            if (version < 2) await MigrateAsync(connection, 2, CreateV2Async, cancellationToken);
            if (version < 3) await MigrateAsync(connection, 3, AddSourceFingerprintAsync, cancellationToken);
            if (version < 4) await MigrateAsync(connection, 4, CreateVideoCacheAsync, cancellationToken);
            if (version < 5) await MigrateAsync(connection, 5, VersionFingerprintsAsync, cancellationToken);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or IOException)
        {
            throw new InvalidDataException("This library could not be upgraded: " + exception.Message +
                (backup is null ? "" : $" Nothing was lost; a copy of the database from before the upgrade is at {backup}."), exception);
        }
    }

    /// <summary>WAL lets readers proceed during a reconcile batch, but needs shared memory that network filesystems do
    /// not provide, so a library on a network share keeps SQLite's rollback journal.</summary>
    private async Task SetJournalModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var requested = (isNetworkVolume ?? StorageVolume.IsNetwork)(Paths.RootDirectory) ? "delete" : "wal";
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA journal_mode={requested}";
        var actual = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(actual, requested, StringComparison.OrdinalIgnoreCase))
            Trace.TraceWarning($"SQLite kept journal mode {actual} for {Paths.DatabasePath} instead of {requested}.");
    }

    private string BackUp(SqliteConnection connection, long version)
    {
        var backup = Path.Combine(Paths.CacheDirectory, $"backup-v{version}.sqlite3");
        if (File.Exists(backup))
            backup = Path.Combine(Paths.CacheDirectory, $"backup-v{version}-{DateTime.UtcNow:yyyyMMddHHmmss}.sqlite3");
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
        destination.Open();
        connection.BackupDatabase(destination);
        return backup;
    }

    private static async Task MigrateAsync(SqliteConnection connection, int target,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> step, CancellationToken cancellationToken)
    {
        await using var transaction = connection.BeginTransaction();
        await step(connection, transaction, cancellationToken);
        await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {target}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static Task CreateV2Async(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
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
            """, cancellationToken);

    private static async Task AddSourceFingerprintAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM pragma_table_info('videos') WHERE name = 'source_fingerprint'", cancellationToken) == 0)
            await ExecuteAsync(connection, transaction, "ALTER TABLE videos ADD COLUMN source_fingerprint TEXT NULL", cancellationToken);
        await ExecuteAsync(connection, transaction,
            "CREATE INDEX IF NOT EXISTS videos_source_fingerprint ON videos(source_fingerprint)", cancellationToken);
    }

    /// <summary>Marks stored source fingerprints with the format that made them (<c>v1:</c> before content sampling
    /// replaced the file name), so a fingerprint can only ever match one made the same way, and makes them unique: the
    /// insert, not a prior lookup, decides that a source is already in the library. Of duplicates from the earlier,
    /// unenforced format, the first-inserted row (lowest rowid) keeps its fingerprint; empty values become NULL.</summary>
    private static Task VersionFingerprintsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
            UPDATE videos SET source_fingerprint = NULL WHERE source_fingerprint = '';
            UPDATE videos SET source_fingerprint = 'v1:' || source_fingerprint
            WHERE source_fingerprint IS NOT NULL AND source_fingerprint NOT GLOB 'v[0-9]:*';
            UPDATE videos SET source_fingerprint = NULL
            WHERE source_fingerprint IS NOT NULL AND rowid NOT IN
                (SELECT MIN(rowid) FROM videos WHERE source_fingerprint IS NOT NULL GROUP BY source_fingerprint);
            DROP INDEX IF EXISTS videos_source_fingerprint;
            CREATE UNIQUE INDEX IF NOT EXISTS videos_source_fingerprint_key ON videos(source_fingerprint) WHERE source_fingerprint IS NOT NULL;
            """, cancellationToken);

    /// <summary>Replaces the FTS search cache with <c>video_cache</c>, makes album names unique by
    /// <see cref="AlbumNames.Key"/>, and rewrites Windows-style stored paths.</summary>
    private static async Task CreateVideoCacheAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, """
            DROP TRIGGER IF EXISTS cache_insert;
            DROP TRIGGER IF EXISTS cache_delete;
            DROP TRIGGER IF EXISTS cache_update;
            DROP TABLE IF EXISTS video_fts;
            DROP TABLE IF EXISTS search_cache;
            CREATE TABLE IF NOT EXISTS video_cache (
                video_id TEXT PRIMARY KEY REFERENCES videos(id) ON DELETE CASCADE,
                record_json TEXT NOT NULL, file_length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL
            );
            """, cancellationToken);
        if (await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM pragma_table_info('albums') WHERE name = 'name_key'", cancellationToken) == 0)
            await ExecuteAsync(connection, transaction, "ALTER TABLE albums ADD COLUMN name_key TEXT NOT NULL DEFAULT ''", cancellationToken);
        await KeyAlbumNamesAsync(connection, transaction, cancellationToken);
        await ExecuteAsync(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS albums_name_key ON albums(name_key)", cancellationToken);
        await MergeWindowsPathDuplicatesAsync(connection, transaction, cancellationToken);
        await ExecuteAsync(connection, transaction, """
            UPDATE videos SET relative_path = replace(relative_path, char(92), '/')
            WHERE instr(relative_path, char(92)) > 0
            """, cancellationToken);
    }

    /// <summary>On Windows <c>Videos\x.mkv</c> and <c>Videos/x.mkv</c> name the same file, so rows that differ only in
    /// the separator are one video: the earliest row is kept, the others' album memberships move to it, and they are
    /// removed.</summary>
    private static async Task MergeWindowsPathDuplicatesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Path)>();
        await using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "SELECT id,relative_path FROM videos ORDER BY rowid";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var group in rows.GroupBy(row => NormalizeRelativePath(row.Path)).Where(group => group.Count() > 1))
        {
            var kept = group.First().Id;
            foreach (var (duplicate, path) in group.Skip(1))
            {
                Trace.TraceWarning($"Merged catalog row {duplicate} ({path}) into {kept}: both name the same file.");
                await using var merge = connection.CreateCommand();
                merge.Transaction = transaction;
                merge.CommandText = """
                    INSERT OR IGNORE INTO album_videos(album_id,video_id) SELECT album_id,$kept FROM album_videos WHERE video_id=$duplicate;
                    DELETE FROM videos WHERE id=$duplicate;
                    """;
                merge.Parameters.AddWithValue("$kept", kept); merge.Parameters.AddWithValue("$duplicate", duplicate);
                await merge.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>Fills <c>name_key</c> for every album, suffixing later albums whose name has the same identity as an
    /// earlier one ("Family", "family" become "Family", "family (2)").</summary>
    private static async Task KeyAlbumNamesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var albums = new List<(string Id, string Name)>();
        await using (var list = connection.CreateCommand())
        {
            list.Transaction = transaction;
            list.CommandText = "SELECT id,name FROM albums ORDER BY rowid";
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) albums.Add((reader.GetString(0), reader.GetString(1)));
        }
        var taken = albums.Select(album => AlbumNames.Key(album.Name)).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, original) in albums)
        {
            var name = AlbumNames.Normalize(original) ?? "Album";
            if (!used.Add(AlbumNames.Key(name)))
            {
                var suffix = 2;
                while (taken.Contains(AlbumNames.Key($"{name} ({suffix})"))) suffix++;
                name = $"{name} ({suffix})";
                taken.Add(AlbumNames.Key(name)); used.Add(AlbumNames.Key(name));
            }
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE albums SET name=$name, name_key=$key WHERE id=$id";
            update.Parameters.AddWithValue("$name", name); update.Parameters.AddWithValue("$key", AlbumNames.Key(name));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>The file for a stored relative path. When the file is missing because its publication has not been
    /// finished yet (a committed video still under its pending name), the pending file is returned, so the video
    /// plays and edits until recovery renames it.</summary>
    public string GetVideoPath(string relativePath)
    {
        var path = GetFinalVideoPath(relativePath);
        if (File.Exists(path) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) return path;
        var pending = Paths.PendingVideoPath(id);
        return File.Exists(pending) ? pending : path;
    }

    /// <summary>The file a stored relative path names, without the pending fallback of <see cref="GetVideoPath"/>. Accepts
    /// <c>\</c> separators from Windows-created rows and refuses paths outside the Videos directory.</summary>
    internal string GetFinalVideoPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        relativePath = NormalizeRelativePath(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("The catalog requires a relative video path.", nameof(relativePath));
        var fullPath = Path.GetFullPath(Path.Combine(Paths.RootDirectory, relativePath));
        var relative = Path.GetRelativePath(Paths.VideosDirectory, fullPath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException("The video path must be inside the library's Videos directory.", nameof(relativePath));
        return fullPath;
    }

    private static string NormalizeRelativePath(string relativePath) => relativePath.Replace('\\', '/');

    /// <summary>Lists videos newest first, skipping rows that cannot be read.</summary>
    public async Task<IReadOnlyList<LibraryVideo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var videos = await ReadVideosAsync(connection, [], cancellationToken);
        return videos.OrderByDescending(video => video.RecordedAt).ThenBy(video => video.Id).ToArray();
    }

    /// <summary>Reads every video, album, membership and cache row in one consistent read transaction.</summary>
    public async Task<CatalogContents> LoadContentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var issues = new List<string>();
        var videos = await ReadVideosAsync(connection, issues, cancellationToken, transaction);
        var known = videos.Select(video => video.Id).ToHashSet();
        var albums = new List<VideoAlbum>();
        await ReadRowsAsync(connection, transaction, "SELECT id,name FROM albums ORDER BY name COLLATE NOCASE,id", issues, "album",
            reader => albums.Add(new VideoAlbum(Guid.Parse(reader.GetString(0)), reader.GetString(1))), cancellationToken);
        var memberships = new List<(Guid, Guid)>();
        await ReadRowsAsync(connection, transaction, "SELECT album_id,video_id FROM album_videos", issues, "album membership",
            reader => memberships.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)))), cancellationToken);
        var cache = new Dictionary<Guid, CatalogCacheEntry>();
        await ReadRowsAsync(connection, transaction, "SELECT video_id,record_json,file_length,modified_ticks FROM video_cache", issues, "cache",
            reader =>
            {
                var id = Guid.Parse(reader.GetString(0));
                if (known.Contains(id)) cache[id] = new CatalogCacheEntry(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3));
            }, cancellationToken);
        return new CatalogContents(videos, albums, memberships, cache, issues);
    }

    /// <summary>Writes cache rows (and refreshed durations) for videos still in the catalog, in one transaction.</summary>
    public async Task WriteCacheAsync(IReadOnlyList<CatalogCacheWrite> writes, CancellationToken cancellationToken = default)
    {
        if (writes.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var write in writes)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO video_cache(video_id,record_json,file_length,modified_ticks)
                SELECT $id,$json,$length,$modified WHERE EXISTS(SELECT 1 FROM videos WHERE id=$id)
                ON CONFLICT(video_id) DO UPDATE SET record_json=excluded.record_json,
                    file_length=excluded.file_length, modified_ticks=excluded.modified_ticks
                """;
            command.Parameters.AddWithValue("$id", write.VideoId.ToString()); command.Parameters.AddWithValue("$json", write.Entry.RecordJson);
            command.Parameters.AddWithValue("$length", write.Entry.FileLength); command.Parameters.AddWithValue("$modified", write.Entry.ModifiedTicks);
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (write.Duration is not { } duration) continue;
            await using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE videos SET duration_ticks=$duration WHERE id=$id";
            update.Parameters.AddWithValue("$id", write.VideoId.ToString()); update.Parameters.AddWithValue("$duration", duration.Ticks);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes video rows in one transaction; their album memberships and cache rows go with them (foreign-key
    /// cascades). Ids without a row are ignored. Touches no file.</summary>
    public async Task DeleteVideosAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "DELETE FROM videos WHERE id=$id";
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Whether a video row with this id exists, without reading the row, so an unreadable row still counts.</summary>
    public async Task<bool> ContainsVideoAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetRelativePathAsync(id, cancellationToken) is not null;

    /// <summary>The stored relative path of the video with this id, or null when no such row exists. Reads only the
    /// path column, so a row that is otherwise unreadable still answers.</summary>
    public async Task<string?> GetRelativePathAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path FROM videos WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteScalarAsync(cancellationToken) is string path ? NormalizeRelativePath(path) : null;
    }

    /// <summary>Every stored relative path, read without parsing the rest of the rows.</summary>
    public async Task<IReadOnlySet<string>> ListRelativePathsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path FROM videos";
        var paths = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(0)) paths.Add(NormalizeRelativePath(reader.GetString(0)));
        return paths;
    }

    /// <summary>True when <paramref name="exception"/> is the insert of a source fingerprint that is already stored.</summary>
    public static bool IsDuplicateSourceFingerprint(SqliteException exception) =>
        exception.SqliteExtendedErrorCode == SqliteConstraintUnique && exception.Message.Contains("source_fingerprint", StringComparison.Ordinal);

    private const int SqliteConstraintUnique = 2067;

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

    /// <summary>Clears <paramref name="fingerprint"/> from the video that holds it, so a new video of the same source can
    /// be registered with it. Does nothing when that video now holds another fingerprint or is gone.</summary>
    public async Task ReleaseSourceFingerprintAsync(Guid videoId, string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE videos SET source_fingerprint = NULL WHERE id = $id AND source_fingerprint = $fingerprint";
        command.Parameters.AddWithValue("$id", videoId.ToString()); command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string VideoColumns ="id,relative_path,display_name,duration_ticks,imported_at,recorded_at,source_fingerprint";

    private async Task<List<LibraryVideo>> ReadVideosAsync(SqliteConnection connection, List<string> issues,
        CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        var videos = new List<LibraryVideo>();
        await ReadRowsAsync(connection, transaction, $"SELECT {VideoColumns} FROM videos", issues, "video",
            reader => videos.Add(ReadVideo(reader)), cancellationToken);
        return videos;
    }

    private static async Task ReadRowsAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, List<string> issues,
        string kind, Action<SqliteDataReader> read, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            try { read(reader); }
            catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidCastException or OverflowException)
            {
                var row = reader.IsDBNull(0) ? "(null)" : reader.GetValue(0).ToString();
                Trace.TraceWarning($"Skipped unreadable {kind} row {row}: {exception}");
                issues.Add($"Skipped an unreadable {kind} entry ({row}): {exception.Message}");
            }
        }
    }

    private LibraryVideo ReadVideo(SqliteDataReader reader)
    {
        var relativePath = NormalizeRelativePath(reader.GetString(1));
        _ = GetVideoPath(relativePath);
        return new LibraryVideo(Guid.Parse(reader.GetString(0)), relativePath, reader.GetString(2),
            TimeSpan.FromTicks(reader.GetInt64(3)), ParseDate(reader.GetString(4)), ParseDate(reader.GetString(5)))
        {
            SourceFingerprint = reader.IsDBNull(6) ? null : reader.GetString(6),
        };
    }

    private static DateTimeOffset ParseDate(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public Task AddAsync(LibraryVideo video, CancellationToken cancellationToken = default) => AddBatchAsync([video], cancellationToken);

    /// <summary>Inserts videos in one transaction. Dates are stored in UTC and paths with <c>/</c> separators.</summary>
    public Task AddBatchAsync(IReadOnlyList<LibraryVideo> videos, CancellationToken cancellationToken = default) =>
        AddBatchAsync(videos, durable: false, cancellationToken);

    /// <summary>Like <see cref="AddBatchAsync(IReadOnlyList{LibraryVideo}, CancellationToken)"/>, but the commit is on
    /// disk when this returns. The library's connections otherwise use <c>synchronous=NORMAL</c>, under which a WAL
    /// commit survives a crash but can be lost on power failure; publishing renames files after this commit, so the
    /// commit must not be lost.</summary>
    public Task AddBatchDurablyAsync(IReadOnlyList<LibraryVideo> videos, CancellationToken cancellationToken = default) =>
        AddBatchAsync(videos, durable: true, cancellationToken);

    private async Task AddBatchAsync(IReadOnlyList<LibraryVideo> videos, bool durable, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // fullfsync makes SQLite flush the drive's own cache on macOS, where plain fsync does not.
        if (durable) await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL; PRAGMA fullfsync=ON", cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var video in videos)
        {
            _ = GetVideoPath(video.RelativePath);
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = $"INSERT INTO videos ({VideoColumns}) VALUES ($id,$path,$name,$duration,$imported,$recorded,$fingerprint)";
            command.Parameters.AddWithValue("$id", video.Id.ToString()); command.Parameters.AddWithValue("$path", NormalizeRelativePath(video.RelativePath));
            command.Parameters.AddWithValue("$name", video.DisplayName); command.Parameters.AddWithValue("$duration", video.Duration.Ticks);
            command.Parameters.AddWithValue("$imported", video.ImportedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$recorded", video.RecordedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$fingerprint", (object?)video.SourceFingerprint ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DatabasePath, Pooling = false, ForeignKeys = true,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
