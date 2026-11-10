using HomeVideoLibrary.Core;
using Microsoft.Data.Sqlite;

namespace HomeVideoLibrary.Infrastructure;

public sealed class AlbumRepository(LibraryCatalog catalog)
{
    public async Task<IReadOnlyList<VideoAlbum>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name FROM albums ORDER BY name_key,name,id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<VideoAlbum>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new VideoAlbum(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return result;
    }

    public Task<VideoAlbum> CreateAsync(string name, CancellationToken cancellationToken = default) => CreateAsync(name, [], cancellationToken);

    public async Task<VideoAlbum> CreateAsync(string name, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        var album = new VideoAlbum(Guid.NewGuid(), NormalizeName(name));
        try { await InsertAsync(album, videoIds, cancellationToken); }
        catch (SqliteException exception) { throw Friendly(exception, album.Name); }
        return album;
    }

    private async Task InsertAsync(VideoAlbum album, IEnumerable<Guid> videoIds, CancellationToken cancellationToken)
    {
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO albums(id,name,name_key) VALUES ($id,$name,$key)";
        command.Parameters.AddWithValue("$id", album.Id.ToString()); command.Parameters.AddWithValue("$name", album.Name);
        command.Parameters.AddWithValue("$key", AlbumNames.Key(album.Name));
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var videoId in videoIds.Distinct())
        {
            await using var membership = connection.CreateCommand(); membership.Transaction = transaction;
            membership.CommandText = "INSERT INTO album_videos(album_id,video_id) VALUES($album,$video)";
            membership.Parameters.AddWithValue("$album", album.Id.ToString()); membership.Parameters.AddWithValue("$video", videoId.ToString());
            await membership.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RenameAsync(Guid albumId, string name, CancellationToken cancellationToken = default)
    {
        name = NormalizeName(name);
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE albums SET name=$name, name_key=$key WHERE id=$id";
        command.Parameters.AddWithValue("$id", albumId.ToString()); command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$key", AlbumNames.Key(name));
        int changed;
        try { changed = await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) { throw Friendly(exception, name); }
        if (changed == 0) throw new InvalidOperationException("This album no longer exists.");
    }

    public async Task DeleteAsync(Guid albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM albums WHERE id=$id";
        command.Parameters.AddWithValue("$id", albumId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task AddVideosAsync(Guid albumId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default) =>
        ChangeMembershipAsync(albumId, videoIds, true, cancellationToken);
    public Task RemoveVideosAsync(Guid albumId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default) =>
        ChangeMembershipAsync(albumId, videoIds, false, cancellationToken);

    private async Task ChangeMembershipAsync(Guid albumId, IEnumerable<Guid> videoIds, bool add, CancellationToken cancellationToken)
    {
        try { await ChangeMembershipCoreAsync(albumId, videoIds, add, cancellationToken); }
        catch (SqliteException exception) { throw Friendly(exception, null); }
    }

    private async Task ChangeMembershipCoreAsync(Guid albumId, IEnumerable<Guid> videoIds, bool add, CancellationToken cancellationToken)
    {
        var ids = videoIds.Distinct().ToArray();
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = add ? "INSERT INTO album_videos(album_id,video_id) VALUES($album,$video) ON CONFLICT DO NOTHING" :
                "DELETE FROM album_videos WHERE album_id=$album AND video_id=$video";
            command.Parameters.AddWithValue("$album", albumId.ToString()); command.Parameters.AddWithValue("$video", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> ListNonMembersAsync(Guid albumId, CancellationToken cancellationToken = default)
    {
        await using var connection = await catalog.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT video_id FROM album_videos WHERE album_id=$album";
        command.Parameters.AddWithValue("$album", albumId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var existing = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken)) existing.Add(Guid.Parse(reader.GetString(0)));
        return (await catalog.ListAsync(cancellationToken)).Where(video => !existing.Contains(video.Id)).ToArray();
    }

    private const int ConstraintUnique = 2067, ConstraintForeignKey = 787;

    private static Exception Friendly(SqliteException exception, string? name) => exception.SqliteExtendedErrorCode switch
    {
        ConstraintUnique when name is not null => new InvalidOperationException($"An album named \u201c{name}\u201d already exists.", exception),
        ConstraintForeignKey => new InvalidOperationException("That album or one of these videos is no longer in the library.", exception),
        _ => exception,
    };

    /// <exception cref="InvalidOperationException">The name is empty.</exception>
    private static string NormalizeName(string name) =>
        AlbumNames.Normalize(name) ?? throw new InvalidOperationException("Give the album a name.");
}
