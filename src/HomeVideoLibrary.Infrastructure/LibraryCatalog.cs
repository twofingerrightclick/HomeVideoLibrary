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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS videos (
                id TEXT PRIMARY KEY, relative_path TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL, duration_ticks INTEGER NOT NULL,
                imported_at TEXT NOT NULL, recorded_at TEXT NOT NULL
            );
            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    public async Task AddAsync(LibraryVideo video, CancellationToken cancellationToken = default)
    {
        _ = GetVideoPath(video.RelativePath);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO videos VALUES ($id,$path,$name,$duration,$imported,$recorded)";
        command.Parameters.AddWithValue("$id", video.Id.ToString());
        command.Parameters.AddWithValue("$path", video.RelativePath);
        command.Parameters.AddWithValue("$name", video.DisplayName);
        command.Parameters.AddWithValue("$duration", video.Duration.Ticks);
        command.Parameters.AddWithValue("$imported", video.ImportedAt.ToString("O"));
        command.Parameters.AddWithValue("$recorded", video.RecordedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Paths.DatabasePath, Pooling = false }.ToString());
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
