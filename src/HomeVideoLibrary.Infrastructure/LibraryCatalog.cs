using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
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
        await using var context = await OpenContextAsync(cancellationToken);
        return await context.LibraryInfo.AsNoTracking().Where(row => row.Id == 1 && LibraryDbContext.SqliteStorageType(row.Name) == "text")
            .Select(row => row.Name).FirstOrDefaultAsync(cancellationToken) ?? DefaultName;
    }

    public async Task SaveNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 120 || name.Any(char.IsControl))
            throw new ArgumentException("Use a library name of up to 120 characters on one line.", nameof(name));
        await using var context = await OpenContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await context.LibraryInfo.Where(row => row.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, name), cancellationToken) == 0)
        {
            context.LibraryInfo.Add(new LibraryInfoRow { Id = 1, Name = name });
            await SaveChangesAsync(context, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
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
        await using var context = await OpenContextAsync(cancellationToken);
        var videos = await ReadVideosAsync(context, [], cancellationToken);
        return videos.OrderByDescending(video => video.RecordedAt).ThenBy(video => video.Id).ToArray();
    }

    /// <summary>Reads every video, album, membership and cache row in one consistent read transaction.</summary>
    public async Task<CatalogContents> LoadContentsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await OpenContextAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(deferred: true);
        await context.Database.UseTransactionAsync(transaction, cancellationToken);
        var issues = new List<string>();
        var videos = await ReadVideosAsync(context, issues, cancellationToken);
        var known = videos.Select(video => video.Id).ToHashSet();
        var albums = new List<VideoAlbum>();
        await ReadRowsAsync(context.Albums.AsNoTracking()
                .OrderBy(row => EF.Functions.Collate(row.Name, "NOCASE")).ThenBy(row => row.Id),
            issues, "album", row => row.Id,
            row => albums.Add(new VideoAlbum(Guid.Parse(row.Id), row.Name)), cancellationToken);
        var memberships = new List<(Guid, Guid)>();
        await ReadRowsAsync(context.AlbumVideos.AsNoTracking(), issues, "album membership", row => row.AlbumId,
            row => memberships.Add((Guid.Parse(row.AlbumId), Guid.Parse(row.VideoId))), cancellationToken);
        var cache = new Dictionary<Guid, CatalogCacheEntry>();
        await ReadRowsAsync(context.VideoCache.AsNoTracking(), issues, "cache", row => row.VideoId,
            row =>
            {
                var id = Guid.Parse(row.VideoId);
                if (known.Contains(id)) cache[id] = new CatalogCacheEntry(row.RecordJson, row.FileLength, row.ModifiedTicks);
            }, cancellationToken);
        return new CatalogContents(videos, albums, memberships, cache, issues);
    }

    /// <summary>Writes cache rows (and refreshed durations) for videos still in the catalog, in one transaction.</summary>
    public async Task WriteCacheAsync(IReadOnlyList<CatalogCacheWrite> writes, CancellationToken cancellationToken = default)
    {
        if (writes.Count == 0) return;
        await using var context = await OpenContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var write in writes)
        {
            var id = write.VideoId.ToString();
            if (!await context.Videos.AnyAsync(row => row.Id == id, cancellationToken)) continue;
            var entry = write.Entry;
            if (await context.VideoCache.Where(row => row.VideoId == id).ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.RecordJson, entry.RecordJson)
                    .SetProperty(row => row.FileLength, entry.FileLength)
                    .SetProperty(row => row.ModifiedTicks, entry.ModifiedTicks), cancellationToken) == 0)
            {
                context.VideoCache.Add(new VideoCacheRow
                {
                    VideoId = id, RecordJson = entry.RecordJson, FileLength = entry.FileLength, ModifiedTicks = entry.ModifiedTicks,
                });
                await SaveChangesAsync(context, cancellationToken);
                context.ChangeTracker.Clear();
            }
            if (write.Duration is not { } duration) continue;
            await context.Videos.Where(row => row.Id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DurationTicks, duration.Ticks), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes video rows in one transaction; their album memberships and cache rows go with them (foreign-key
    /// cascades). Ids without a row are ignored. Touches no file. A confirmed deletion is recorded in the same
    /// transaction, even if its active row was already removed; callers supply history only after successful file removal.</summary>
    public async Task DeleteVideosAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default, DeletionHistory? deletion = null)
    {
        if (ids.Count == 0) return;
        await using var context = await OpenContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (deletion is not null)
        {
            var removedAt = deletion.RemovedAt.ToUniversalTime().ToString("O");
            var revision = deletion.Revision.ToString();
            if (await context.DeletionHistory.Where(row => row.Fingerprint == deletion.Fingerprint)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Title, deletion.Title)
                        .SetProperty(row => row.RemovedAt, removedAt).SetProperty(row => row.Kind, (int)deletion.Kind)
                        .SetProperty(row => row.Revision, revision), cancellationToken) == 0)
            {
                context.DeletionHistory.Add(new DeletionHistoryRow
                {
                    Fingerprint = deletion.Fingerprint, Title = deletion.Title, RemovedAt = removedAt,
                    Kind = (int)deletion.Kind, Revision = revision,
                });
                await SaveChangesAsync(context, cancellationToken);
            }
        }
        foreach (var id in ids)
        {
            var key = id.ToString();
            await context.Videos.Where(row => row.Id == key).ExecuteDeleteAsync(cancellationToken);
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
        await using var context = await OpenContextAsync(cancellationToken);
        var key = id.ToString();
        var path = await context.Videos.AsNoTracking().Where(row => row.Id == key && LibraryDbContext.SqliteStorageType(row.RelativePath) == "text")
            .Select(row => row.RelativePath).FirstOrDefaultAsync(cancellationToken);
        return path is null ? null : NormalizeRelativePath(path);
    }

    /// <summary>Every stored relative path, read without parsing the rest of the rows.</summary>
    public async Task<IReadOnlySet<string>> ListRelativePathsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await OpenContextAsync(cancellationToken);
        var paths = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        await foreach (var path in context.Videos.AsNoTracking().Select(row => row.RelativePath)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
            if (path is not null) paths.Add(NormalizeRelativePath(path));
        return paths;
    }

    /// <summary>True when <paramref name="exception"/> is the insert of a source fingerprint that is already stored.</summary>
    public static bool IsDuplicateSourceFingerprint(SqliteException exception) =>
        exception.SqliteExtendedErrorCode == SqliteConstraintUnique && exception.Message.Contains("source_fingerprint", StringComparison.Ordinal);

    private const int SqliteConstraintUnique = 2067;

    public async Task<LibraryVideo?> FindBySourceFingerprintAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using var context = await OpenContextAsync(cancellationToken);
        var row = await context.Videos.AsNoTracking().FirstOrDefaultAsync(row => row.SourceFingerprint == fingerprint, cancellationToken);
        return row is null ? null : ReadVideo(row);
    }

    /// <summary>Clears <paramref name="fingerprint"/> from the video that holds it, so a new video of the same source can
    /// be registered with it. Does nothing when that video now holds another fingerprint or is gone.</summary>
    public async Task ReleaseSourceFingerprintAsync(Guid videoId, string fingerprint, CancellationToken cancellationToken = default)
    {
        await using var context = await OpenContextAsync(cancellationToken);
        var id = videoId.ToString();
        await context.Videos.Where(row => row.Id == id && row.SourceFingerprint == fingerprint)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SourceFingerprint, (string?)null), cancellationToken);
    }

    public async Task<DeletionHistory?> FindDeletionAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        await using var context = await OpenContextAsync(cancellationToken);
        var row = await context.DeletionHistory.AsNoTracking().FirstOrDefaultAsync(row => row.Fingerprint == fingerprint, cancellationToken);
        return row is null ? null : new DeletionHistory(fingerprint, row.Title, ParseDate(row.RemovedAt), (DeletionKind)row.Kind)
        {
            Revision = Guid.Parse(row.Revision),
        };
    }

    private async Task<List<LibraryVideo>> ReadVideosAsync(LibraryDbContext context, List<string> issues,
        CancellationToken cancellationToken)
    {
        var videos = new List<LibraryVideo>();
        await ReadRowsAsync(context.Videos.AsNoTracking(), issues, "video", row => row.Id,
            row => videos.Add(ReadVideo(row)), cancellationToken);
        return videos;
    }

    private static async Task ReadRowsAsync<T>(IQueryable<T> query, List<string> issues,
        string kind, Expression<Func<T, string>> key, Action<T> read, CancellationToken cancellationToken)
    {
        var getKey = key.Compile();
        List<T> rows;
        try { rows = await query.ToListAsync(cancellationToken); }
        catch (Exception exception) when (IsUnreadableRow(exception))
        {
            // A corrupt SQLite storage value may fail in EF's materializer before our parser runs.
            // Retry only this exceptional scan one key at a time so its other rows remain readable.
            var keys = await query.Select(key).ToListAsync(cancellationToken);
            foreach (var id in keys.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var predicate = Expression.Lambda<Func<T, bool>>(
                        Expression.Equal(key.Body, Expression.Constant(id, typeof(string))), key.Parameters);
                    foreach (var row in await query.Where(predicate).ToListAsync(cancellationToken))
                        read(row);
                }
                catch (Exception rowException) when (IsUnreadableRow(rowException))
                {
                    ReportUnreadableRow(issues, kind, id, rowException);
                }
            }
            return;
        }
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { read(row); }
            catch (Exception exception) when (IsUnreadableRow(exception))
            {
                ReportUnreadableRow(issues, kind, getKey(row), exception);
            }
        }
    }

    private static bool IsUnreadableRow(Exception exception) =>
        exception is FormatException or ArgumentException or InvalidCastException or OverflowException;

    private static void ReportUnreadableRow(List<string> issues, string kind, string? key, Exception exception)
    {
        var row = key ?? "(null)";
        Trace.TraceWarning($"Skipped unreadable {kind} row {row}: {exception}");
        issues.Add($"Skipped an unreadable {kind} entry ({row}): {exception.Message}");
    }

    private LibraryVideo ReadVideo(VideoRow row)
    {
        var relativePath = NormalizeRelativePath(row.RelativePath);
        _ = GetVideoPath(relativePath);
        return new LibraryVideo(Guid.Parse(row.Id), relativePath, row.DisplayName,
            TimeSpan.FromTicks(row.DurationTicks), ParseDate(row.ImportedAt), ParseDate(row.RecordedAt))
        {
            SourceFingerprint = row.SourceFingerprint,
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
        await using var context = await OpenContextAsync(cancellationToken);
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        // fullfsync makes SQLite flush the drive's own cache on macOS, where plain fsync does not.
        if (durable) await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL; PRAGMA fullfsync=ON", cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var video in videos)
        {
            _ = GetVideoPath(video.RelativePath);
            context.Videos.Add(new VideoRow
            {
                Id = video.Id.ToString(), RelativePath = NormalizeRelativePath(video.RelativePath), DisplayName = video.DisplayName,
                DurationTicks = video.Duration.Ticks, ImportedAt = video.ImportedAt.ToUniversalTime().ToString("O"),
                RecordedAt = video.RecordedAt.ToUniversalTime().ToString("O"), SourceFingerprint = video.SourceFingerprint,
            });
            await SaveChangesAsync(context, cancellationToken);
            // A repeated id must reach SQLite's constraint handling rather than EF's identity-map error.
            context.ChangeTracker.Clear();
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SaveChangesAsync(LibraryDbContext context, CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException sqlite)
        {
            // Import callers classify SQLite's unique source-fingerprint constraint directly.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sqlite).Throw();
            throw;
        }
    }

    /// <summary>Opens a context with the runtime connection policy; disposing it also disposes its connection.</summary>
    internal async Task<LibraryDbContext> OpenContextAsync(CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(cancellationToken);
        try
        {
            var options = new DbContextOptionsBuilder<LibraryDbContext>()
                .UseSqlite(connection, contextOwnsConnection: true).Options;
            return new LibraryDbContext(options);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
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
