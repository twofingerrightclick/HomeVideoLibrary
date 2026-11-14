using System.Diagnostics;
using System.Globalization;
using HomeVideoLibrary.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
    public LibraryPaths Paths { get; } = paths;

    public const string DefaultName = "HOME VIDEO LIBRARY";

    public async Task<string> ReadNameAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM library_info WHERE id = 1";
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? DefaultName;
    }

    public async Task SaveNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Use a library name of up to 120 characters on one line.", nameof(name));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO library_info (id, name) VALUES (1, $name) ON CONFLICT(id) DO UPDATE SET name = excluded.name";
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Creates the catalog using EF migrations. Pre-release catalogs without valid EF history must be
    /// opened with their original application; they are never upgraded, repaired or discarded here.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var context = new LibraryDbContext(LibraryDbContext.Options(Paths.DatabasePath));
        await ValidateHistoryAsync(context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Paths.RootDirectory);
        Directory.CreateDirectory(Paths.VideosDirectory);
        Directory.CreateDirectory(Paths.CacheDirectory);
        await using var connection = await OpenAsync(cancellationToken);
        context.Database.SetDbConnection(connection, contextOwnsConnection: false);
        await SetJournalModeAsync(connection, cancellationToken);
        await context.Database.MigrateAsync(cancellationToken);
        // SQLite's EF database creator can enable WAL. Restore the actual volume policy after migration.
        await SetJournalModeAsync(connection, cancellationToken);
    }

    private async Task ValidateHistoryAsync(LibraryDbContext context, CancellationToken cancellationToken)
    {
        if (!File.Exists(Paths.DatabasePath)) return;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var tables = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE name NOT GLOB 'sqlite_*'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        }
        if (tables.Count == 0) return;
        const string unsupported = "This catalog has no valid EF migration history or was created by an unsupported version. " +
            "No changes were made. Open it with its original application, or create a fresh library. " +
            "Keep the original database: it contains albums and memberships that cannot be rebuilt from MKVs.";
        var hasCatalogObjects = tables.Any(name => name is not ("__EFMigrationsHistory" or "__EFMigrationsLock"));
        if (!tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal))
        {
            if (!hasCatalogObjects) return;
            throw new InvalidDataException(unsupported);
        }
        var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MigrationId, ProductVersion FROM __EFMigrationsHistory";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)) ||
                    !applied.Add(reader.GetString(0)))
                    throw new InvalidDataException(unsupported);
                if (!known.Contains(reader.GetString(0)))
                    throw new InvalidDataException("This library requires a newer version of Home Video Library to open.");
            }
        }
        catch (SqliteException exception) { throw new InvalidDataException(unsupported, exception); }
        // A cancelled first migration can leave empty EF bookkeeping outside the schema transaction.
        // Only that empty state is retryable; user tables without applied history remain unsupported.
        if (applied.Count == 0 && hasCatalogObjects) throw new InvalidDataException(unsupported);
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
    /// cascades). Ids without a row are ignored. Touches no file. A confirmed deletion is recorded in the same
    /// transaction, even if its active row was already removed; callers supply history only after successful file removal.</summary>
    public async Task DeleteVideosAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default, DeletionHistory? deletion = null)
    {
        if (ids.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        if (deletion is not null)
        {
            await using var history = connection.CreateCommand(); history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO deletion_history(fingerprint,title,removed_at,kind,revision) VALUES($fingerprint,$title,$removed,$kind,$revision)
                ON CONFLICT(fingerprint) DO UPDATE SET title=excluded.title,removed_at=excluded.removed_at,kind=excluded.kind,revision=excluded.revision
                """;
            history.Parameters.AddWithValue("$fingerprint", deletion.Fingerprint);
            history.Parameters.AddWithValue("$title", deletion.Title);
            history.Parameters.AddWithValue("$removed", deletion.RemovedAt.ToUniversalTime().ToString("O"));
            history.Parameters.AddWithValue("$kind", (int)deletion.Kind);
            history.Parameters.AddWithValue("$revision", deletion.Revision.ToString());
            await history.ExecuteNonQueryAsync(cancellationToken);
        }
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

    public async Task<DeletionHistory?> FindDeletionAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title,removed_at,kind,revision FROM deletion_history WHERE fingerprint=$fingerprint";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DeletionHistory(fingerprint, reader.GetString(0), ParseDate(reader.GetString(1)), (DeletionKind)reader.GetInt32(2)) { Revision = Guid.Parse(reader.GetString(3)) } : null;
    }

    private const string VideoColumns = "id,relative_path,display_name,duration_ticks,imported_at,recorded_at,source_fingerprint";

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

}
