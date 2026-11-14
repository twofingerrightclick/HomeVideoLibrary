using HomeVideoLibrary.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeVideoLibrary.Infrastructure;

public sealed class AlbumRepository(LibraryCatalog catalog)
{
    public async Task<IReadOnlyList<VideoAlbum>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await catalog.OpenContextAsync(cancellationToken);
        var rows = await context.Albums.AsNoTracking().OrderBy(row => row.NameKey).ThenBy(row => row.Name).ThenBy(row => row.Id)
            .Select(row => new { row.Id, row.Name }).ToListAsync(cancellationToken);
        return rows.Select(row => new VideoAlbum(Guid.Parse(row.Id), row.Name)).ToArray();
    }

    public Task<VideoAlbum> CreateAsync(string name, CancellationToken cancellationToken = default) => CreateAsync(name, [], cancellationToken);

    public async Task<VideoAlbum> CreateAsync(string name, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        var album = new VideoAlbum(Guid.NewGuid(), NormalizeName(name));
        try { await InsertAsync(album, videoIds, cancellationToken); }
        catch (SqliteException exception) { throw Friendly(exception, album.Name); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException sqlite) { throw Friendly(sqlite, album.Name); }
        return album;
    }

    private async Task InsertAsync(VideoAlbum album, IEnumerable<Guid> videoIds, CancellationToken cancellationToken)
    {
        await using var context = await catalog.OpenContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        context.Albums.Add(new AlbumRow { Id = album.Id.ToString(), Name = album.Name, NameKey = AlbumNames.Key(album.Name) });
        await context.SaveChangesAsync(cancellationToken);
        foreach (var videoId in videoIds.Distinct())
        {
            context.AlbumVideos.Add(new AlbumVideoRow { AlbumId = album.Id.ToString(), VideoId = videoId.ToString() });
            await context.SaveChangesAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RenameAsync(Guid albumId, string name, CancellationToken cancellationToken = default)
    {
        name = NormalizeName(name);
        await using var context = await catalog.OpenContextAsync(cancellationToken);
        var id = albumId.ToString();
        var key = AlbumNames.Key(name);
        int changed;
        try
        {
            changed = await context.Albums.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Name, name).SetProperty(row => row.NameKey, key), cancellationToken);
        }
        catch (SqliteException exception) { throw Friendly(exception, name); }
        if (changed == 0) throw new InvalidOperationException("This album no longer exists.");
    }

    public async Task DeleteAsync(Guid albumId, CancellationToken cancellationToken = default)
    {
        await using var context = await catalog.OpenContextAsync(cancellationToken);
        var id = albumId.ToString();
        await context.Albums.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    public Task AddVideosAsync(Guid albumId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default) =>
        ChangeMembershipAsync(albumId, videoIds, true, cancellationToken);
    public Task RemoveVideosAsync(Guid albumId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default) =>
        ChangeMembershipAsync(albumId, videoIds, false, cancellationToken);

    private async Task ChangeMembershipAsync(Guid albumId, IEnumerable<Guid> videoIds, bool add, CancellationToken cancellationToken)
    {
        try { await ChangeMembershipCoreAsync(albumId, videoIds, add, cancellationToken); }
        catch (SqliteException exception) { throw Friendly(exception, null); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException sqlite) { throw Friendly(sqlite, null); }
    }

    private async Task ChangeMembershipCoreAsync(Guid albumId, IEnumerable<Guid> videoIds, bool add, CancellationToken cancellationToken)
    {
        var ids = videoIds.Distinct().ToArray();
        await using var context = await catalog.OpenContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var album = albumId.ToString();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = id.ToString();
            var membership = context.AlbumVideos.Where(row => row.AlbumId == album && row.VideoId == video);
            if (!add) await membership.ExecuteDeleteAsync(cancellationToken);
            else if (!await membership.AnyAsync(cancellationToken))
            {
                // The immediate write transaction keeps this existence check and insert atomic with other writers.
                context.AlbumVideos.Add(new AlbumVideoRow { AlbumId = album, VideoId = video });
                await context.SaveChangesAsync(cancellationToken);
                context.ChangeTracker.Clear();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LibraryVideo>> ListNonMembersAsync(Guid albumId, CancellationToken cancellationToken = default)
    {
        await using var context = await catalog.OpenContextAsync(cancellationToken);
        var key = albumId.ToString();
        var ids = await context.AlbumVideos.AsNoTracking().Where(row => row.AlbumId == key)
            .Select(row => row.VideoId).ToListAsync(cancellationToken);
        var existing = ids.Select(Guid.Parse).ToHashSet();
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
