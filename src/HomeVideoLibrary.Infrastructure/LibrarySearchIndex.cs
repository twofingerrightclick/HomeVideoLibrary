using System.Globalization;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

public sealed partial class LibrarySearchIndex(LibraryCatalog catalog, IVideoMetadataService metadata)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> VideoUpdates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async Task RefreshAsync(bool force = false, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var videos = await catalog.ListAsync(cancellationToken);
        var completed = 0;
        foreach (var video in videos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? error = null;
            try { await UpdateVideoAsync(video, force, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { error = exception.Message; }
            progress?.Report(new IndexProgress(++completed, videos.Count, video.DisplayName, error));
        }
    }

    public async Task UpdateVideoAsync(LibraryVideo video, bool force = true, CancellationToken cancellationToken = default)
    {
        var path = catalog.GetVideoPath(video.RelativePath);
        var gate = VideoUpdates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await UpdateVideoCoreAsync(video, force, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task UpdateVideoCoreAsync(LibraryVideo video, bool force, CancellationToken cancellationToken)
    {
        var path = catalog.GetVideoPath(video.RelativePath);
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc.Ticks;
        await using var connection = await catalog.OpenAsync(cancellationToken);
        if (!force)
        {
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM search_cache WHERE video_id=$id AND file_length=$length AND modified_ticks=$modified";
            check.Parameters.AddWithValue("$id", video.Id.ToString()); check.Parameters.AddWithValue("$length", length); check.Parameters.AddWithValue("$modified", modified);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0) return;
        }
        var content = await metadata.ReadAsync(video, cancellationToken);
        var recordedAt = content.RecordedAt;
        if (recordedAt is null)
        {
            await using var originalDate = connection.CreateCommand();
            originalDate.CommandText = "SELECT recorded_at FROM videos WHERE id=$id";
            originalDate.Parameters.AddWithValue("$id", video.Id.ToString());
            recordedAt = DateTimeOffset.Parse((string)(await originalDate.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("The video is no longer registered in this library.")), CultureInfo.InvariantCulture);
        }
        var after = new FileInfo(path);
        if (after.Length != length || after.LastWriteTimeUtc.Ticks != modified)
            throw new IOException("The video changed while its metadata was being indexed. Refresh to retry.");
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO search_cache(video_id,title,description,tags,chapters,recorded_at,file_length,modified_ticks)
            VALUES($id,$title,$description,$tags,$chapters,$recorded,$length,$modified)
            ON CONFLICT(video_id) DO UPDATE SET title=excluded.title,description=excluded.description,tags=excluded.tags,
                chapters=excluded.chapters,recorded_at=excluded.recorded_at,file_length=excluded.file_length,modified_ticks=excluded.modified_ticks
            """;
        command.Parameters.AddWithValue("$id", video.Id.ToString());
        command.Parameters.AddWithValue("$title", content.Title); command.Parameters.AddWithValue("$description", content.Description);
        command.Parameters.AddWithValue("$tags", string.Join('\n', content.Tags));
        command.Parameters.AddWithValue("$chapters", string.Join('\n', content.Chapters.Select(chapter => chapter.Title + "\n" + chapter.Description + "\n" + string.Join('\n', chapter.Tags))));
        command.Parameters.AddWithValue("$recorded", recordedAt.Value.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$length", length); command.Parameters.AddWithValue("$modified", modified);
        await command.ExecuteNonQueryAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> SearchAsync(string query, Guid? albumId = null, bool excludeAlbumMembers = false,
        CancellationToken cancellationToken = default)
    {
        var match = BuildMatch(query);
        var hasText = !string.IsNullOrWhiteSpace(query);
        if (hasText && match.Length == 0) return [];
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT v.id,v.relative_path,v.display_name,v.duration_ticks,v.imported_at,
                COALESCE(c.recorded_at,v.recorded_at),COALESCE(c.title,''),COALESCE(c.description,'')
            FROM videos v LEFT JOIN search_cache c ON c.video_id=v.id
            {(hasText ? "JOIN video_fts ON video_fts.rowid=c.rowid" : "")}
            WHERE {(hasText ? "video_fts MATCH $match AND" : "")}
                ($album IS NULL OR {(excludeAlbumMembers ? "NOT" : "")} EXISTS(SELECT 1 FROM album_videos av WHERE av.album_id=$album AND av.video_id=v.id))
            ORDER BY {(hasText ? "bm25(video_fts,3.0,2.0,3.0,1.0)," : "")} julianday(COALESCE(c.recorded_at,v.recorded_at)) DESC,v.id
            """;
        if (hasText) command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$album", albumId is { } id ? id.ToString() : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<LibraryVideo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var relative = reader.GetString(1); _ = catalog.GetVideoPath(relative);
            results.Add(new LibraryVideo(Guid.Parse(reader.GetString(0)), relative, reader.GetString(2), TimeSpan.FromTicks(reader.GetInt64(3)),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture))
                { Title = reader.GetString(6), Description = reader.GetString(7) });
        }
        return results;
    }

    private static string BuildMatch(string query) => string.Join(" AND ", TokenRegex().Matches(query).Cast<Match>().Take(32)
        .Select(match => "\"" + match.Value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*"));

    [GeneratedRegex(@"[\p{L}\p{M}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
