using System.Collections.Immutable;
using System.Diagnostics;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>An immutable, consistent view of the whole library. Safe to read from any thread.</summary>
public sealed class LibrarySnapshot
{
    private IReadOnlyList<VideoRecord>? _chronological;

    internal LibrarySnapshot(ImmutableDictionary<Guid, VideoRecord> videos, ImmutableArray<VideoAlbum> albums,
        ImmutableDictionary<Guid, ImmutableHashSet<Guid>> albumMembers, TagIndex tags)
    {
        Videos = videos; Albums = albums; AlbumMembers = albumMembers; Tags = tags;
    }

    public static LibrarySnapshot Empty { get; } = new(ImmutableDictionary<Guid, VideoRecord>.Empty, [],
        ImmutableDictionary<Guid, ImmutableHashSet<Guid>>.Empty, TagIndex.Empty);

    public ImmutableDictionary<Guid, VideoRecord> Videos { get; }
    /// <summary>Sorted by <see cref="AlbumNames.Order"/>.</summary>
    public ImmutableArray<VideoAlbum> Albums { get; }
    public ImmutableDictionary<Guid, ImmutableHashSet<Guid>> AlbumMembers { get; }
    public TagIndex Tags { get; }

    /// <summary>Every video, newest recorded first (ties by id).</summary>
    public IReadOnlyList<VideoRecord> Chronological => _chronological ??=
        Videos.Values.OrderByDescending(record => record.RecordedAt).ThenBy(record => record.Id).ToArray();

    public VideoRecord? Find(Guid videoId) => Videos.GetValueOrDefault(videoId);
    public VideoAlbum? FindAlbum(Guid albumId) => Albums.FirstOrDefault(album => album.Id == albumId);
    public ImmutableHashSet<Guid> MembersOf(Guid albumId) => AlbumMembers.GetValueOrDefault(albumId) ?? [];
    public IEnumerable<VideoAlbum> AlbumsContaining(Guid videoId) => Albums.Where(album => MembersOf(album.Id).Contains(videoId));

    internal LibrarySnapshot With(ImmutableDictionary<Guid, VideoRecord>? videos = null, ImmutableArray<VideoAlbum>? albums = null,
        ImmutableDictionary<Guid, ImmutableHashSet<Guid>>? albumMembers = null, TagIndex? tags = null) =>
        new(videos ?? Videos, albums is { } sorted ? Sort(sorted) : Albums, albumMembers ?? AlbumMembers, tags ?? Tags);

    internal static ImmutableArray<VideoAlbum> Sort(IEnumerable<VideoAlbum> albums) =>
        [.. AlbumNames.Order(albums, album => album.Name).ThenBy(album => album.Id)];
}

/// <summary>What changed between the previous snapshot and <see cref="Snapshot"/>.</summary>
/// <param name="IsReset">The whole library was (re)loaded; rebuild every view from <see cref="Snapshot"/>.</param>
public sealed record LibraryDelta(LibrarySnapshot Snapshot, IReadOnlyCollection<Guid> Added, IReadOnlyCollection<Guid> Updated,
    IReadOnlyCollection<Guid> Removed, bool AlbumsChanged, bool TagsChanged, bool IsReset = false);

public sealed record LibraryLoadResult(int Videos, int Pending, IReadOnlyList<string> Issues);

public sealed record ReconcileError(Guid VideoId, string VideoName, string Message);

/// <param name="Read">Videos whose MKV metadata was read.</param>
/// <param name="Missing">Videos whose file is currently missing.</param>
public sealed record ReconcileResult(int Total, int Read, int Missing, IReadOnlyList<ReconcileError> Errors);

public sealed record AlbumMembershipChange(VideoAlbum Album, int Changed, int Unchanged);

/// <summary>The in-memory library: catalog membership and albums from SQLite plus each MKV's text metadata, published as
/// immutable snapshots. Readers use <see cref="Snapshot"/> without locking; every change goes through one writer and is
/// announced by <see cref="Changed"/>. Every write to an MKV, the catalog or an album must be published through this model.</summary>
public sealed class LibraryModel
{
    private const int BatchSize = 25;
    private readonly IVideoTextMetadataReader _reader;
    private readonly AlbumRepository _albums;
    private readonly int _parallelism;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;

    /// <param name="readParallelism">Concurrent MKV reads during reconcile; defaults to min(4, cores / 2).</param>
    public LibraryModel(LibraryCatalog catalog, IVideoTextMetadataReader reader, int? readParallelism = null)
    {
        Catalog = catalog; _reader = reader; _albums = new AlbumRepository(catalog);
        _parallelism = readParallelism ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(_parallelism, 1, nameof(readParallelism));
    }

    public LibraryCatalog Catalog { get; }
    public LibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Raised on the writer's thread after each published change, in order. Handlers must not block or call
    /// back into the model synchronously; marshal to the UI thread instead.</summary>
    public event Action<LibraryDelta>? Changed;

    public string GetVideoPath(VideoRecord record) => Catalog.GetVideoPath(record.RelativePath);

    /// <summary>Publishes the catalog and cached MKV metadata without starting any process or touching video files.
    /// Videos without a usable cache entry are <see cref="VideoRecordState.Pending"/>.</summary>
    public Task<LibraryLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        // The catalog is read inside the writer, so no other model write can land between the read and the publish.
        MutateAsync(async _ =>
        {
            var contents = await Catalog.LoadContentsAsync(cancellationToken);
            var videos = contents.Videos.ToImmutableDictionary(video => video.Id, video => FromCatalog(video, contents.Cache));
            var snapshot = new LibrarySnapshot(videos, LibrarySnapshot.Sort(contents.Albums), Memberships(contents, videos),
                TagIndex.Build(videos.Values));
            var result = new LibraryLoadResult(videos.Count, videos.Values.Count(record => record.State == VideoRecordState.Pending), contents.Issues);
            return (snapshot, new LibraryDelta(snapshot, [], [], [], true, true, IsReset: true), result);
        }, cancellationToken);

    /// <summary>Brings the snapshot up to date with the catalog and the files: new catalog rows are added, files are
    /// stat-checked, and new, changed or unreadable files (all of them when <paramref name="force"/>) are read with
    /// <see cref="IVideoTextMetadataReader.ReadTextAsync"/>. Results are cached and published in batches, so a
    /// cancellation keeps every finished batch. Per-file failures are collected, not thrown.</summary>
    public async Task<ReconcileResult> ReconcileAsync(bool force = false, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _reconcile.WaitAsync(cancellationToken);
        try
        {
            await SyncCatalogAsync(cancellationToken);
            var toRead = await StatAsync(force, cancellationToken);
            var errors = new List<ReconcileError>();
            var completed = 0;
            foreach (var batch in toRead.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcomes = new ReadOutcome[batch.Length];
                await Parallel.ForEachAsync(Enumerable.Range(0, batch.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken },
                    async (index, token) =>
                    {
                        var outcome = outcomes[index] = await ReadAsync(batch[index], token);
                        progress?.Report(new IndexProgress(Interlocked.Increment(ref completed), toRead.Count, batch[index].Label, outcome.Error));
                    });
                errors.AddRange((await CommitAsync(batch.Zip(outcomes, (record, outcome) => new PendingCommit(record.Id, null, outcome)).ToArray(),
                    publishStaleAsPending: false)).Errors);
            }
            var snapshot = Snapshot;
            return new ReconcileResult(snapshot.Videos.Count, toRead.Count,
                snapshot.Videos.Values.Count(record => record.IsMissing), errors);
        }
        finally { _reconcile.Release(); }
    }

    /// <summary>The cheap part of reconcile: stats every file, publishes missing and restored files, and returns how
    /// many videos need a read. Returns 0 without doing anything while a reconcile is running.</summary>
    public async Task<int> StatPassAsync(CancellationToken cancellationToken = default)
    {
        if (!await _reconcile.WaitAsync(0, cancellationToken)) return 0;
        try { return (await StatAsync(false, cancellationToken)).Count; }
        finally { _reconcile.Release(); }
    }

    /// <summary>Reads one video's MKV and publishes it, adding it to the snapshot when new (after an import or clip
    /// edit) or replacing it (after a metadata save). Read failures do not throw: the returned record is then
    /// <see cref="VideoRecordState.Unreadable"/> or <see cref="VideoRecord.IsMissing"/>. If the file keeps changing
    /// while it is read, the record is published as <see cref="VideoRecordState.Pending"/> so the next stat pass reads it.</summary>
    /// <exception cref="InvalidOperationException">The video has no catalog row (it was removed, for example while a
    /// save was being published); it is not added back.</exception>
    public async Task<VideoRecord> UpsertFromFileAsync(LibraryVideo video, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        var path = Catalog.GetVideoPath(video.RelativePath);
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await ReadAsync(path, cancellationToken);
            var commit = await CommitAsync([new PendingCommit(video.Id, video, outcome)], publishStaleAsPending: attempt >= 3);
            if (commit.Applied > 0) return Snapshot.Find(video.Id)!;
            if (commit.Unregistered > 0) throw new InvalidOperationException($"“{video.DisplayName}” is no longer in the library.");
        }
    }

    /// <summary>Removes videos from the library: deletes their catalog rows, which takes their album memberships and
    /// cached metadata with them, and publishes them as removed. Touches no file, so a caller deleting the files does that
    /// first: a crash in between leaves rows whose files are missing, which the user can remove, rather than rows for
    /// files that were never deleted. Ids not in the library are ignored.</summary>
    public Task RemoveVideosAsync(IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        var ids = videoIds.Distinct().ToArray();
        return MutateAsync(async snapshot =>
        {
            await Catalog.DeleteVideosAsync(ids, cancellationToken);
            var removed = ids.Where(snapshot.Videos.ContainsKey).ToArray();
            if (removed.Length == 0) return (snapshot, (LibraryDelta?)null, true);
            var members = snapshot.AlbumMembers;
            foreach (var (albumId, videos) in snapshot.AlbumMembers)
                if (videos.Overlaps(removed)) members = members.SetItem(albumId, videos.Except(removed));
            var tags = snapshot.Tags.Apply(removed.Select(id => ((VideoRecord?)snapshot.Videos[id], (VideoRecord?)null)));
            var next = snapshot.With(snapshot.Videos.RemoveRange(removed), albumMembers: members, tags: tags);
            return (next, new LibraryDelta(next, [], [], removed, !ReferenceEquals(members, snapshot.AlbumMembers),
                !ReferenceEquals(tags, snapshot.Tags)), true);
        }, cancellationToken);
    }

    public Task<VideoAlbum> CreateAlbumAsync(string name, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default)
    {
        var ids = videoIds.Distinct().ToArray();
        return MutateAsync(async snapshot =>
        {
            var album = await _albums.CreateAsync(name, ids, cancellationToken);
            var next = snapshot.With(albums: snapshot.Albums.Add(album), albumMembers: snapshot.AlbumMembers.SetItem(album.Id, [.. ids]));
            return (next, AlbumDelta(next), album);
        }, cancellationToken);
    }

    public Task<VideoAlbum> RenameAlbumAsync(Guid albumId, string name, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            await _albums.RenameAsync(albumId, name, cancellationToken);
            var renamed = new VideoAlbum(albumId, AlbumNames.Normalize(name)!);
            var next = snapshot.With(albums: snapshot.Albums.Where(album => album.Id != albumId).Append(renamed).ToImmutableArray());
            return (next, AlbumDelta(next), renamed);
        }, cancellationToken);

    public Task DeleteAlbumAsync(Guid albumId, CancellationToken cancellationToken = default) =>
        MutateAsync(async snapshot =>
        {
            await _albums.DeleteAsync(albumId, cancellationToken);
            var next = snapshot.With(albums: snapshot.Albums.Where(album => album.Id != albumId).ToImmutableArray(),
                albumMembers: snapshot.AlbumMembers.Remove(albumId));
            return (next, AlbumDelta(next), true);
        }, cancellationToken);

    /// <summary>Adds videos to an album; videos already in it are counted as unchanged.</summary>
    public Task<AlbumMembershipChange> AddToAlbumAsync(Guid albumId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default) =>
        ChangeMembershipAsync(albumId, videoIds, add: true, cancellationToken);

    public Task<AlbumMembershipChange> RemoveFromAlbumAsync(Guid albumId, IEnumerable<Guid> videoIds, CancellationToken cancellationToken = default) =>
        ChangeMembershipAsync(albumId, videoIds, add: false, cancellationToken);

    private Task<AlbumMembershipChange> ChangeMembershipAsync(Guid albumId, IEnumerable<Guid> videoIds, bool add, CancellationToken cancellationToken)
    {
        var ids = videoIds.Distinct().ToArray();
        return MutateAsync(async snapshot =>
        {
            var album = snapshot.FindAlbum(albumId) ?? throw new InvalidOperationException("This album no longer exists.");
            if (add) await _albums.AddVideosAsync(albumId, ids, cancellationToken);
            else await _albums.RemoveVideosAsync(albumId, ids, cancellationToken);
            var members = snapshot.MembersOf(albumId);
            var changed = ids.Count(id => members.Contains(id) != add);
            var next = snapshot.With(albumMembers: snapshot.AlbumMembers.SetItem(albumId, add ? members.Union(ids) : members.Except(ids)));
            return (next, AlbumDelta(next), new AlbumMembershipChange(album, changed, ids.Length - changed));
        }, cancellationToken);
    }

    private static LibraryDelta AlbumDelta(LibrarySnapshot snapshot) => new(snapshot, [], [], [], AlbumsChanged: true, TagsChanged: false);

    private Task SyncCatalogAsync(CancellationToken cancellationToken) =>
        MutateAsync(async snapshot =>
        {
            // Read inside the writer: an import or album edit published meanwhile must not be reverted by an older read.
            var contents = await Catalog.LoadContentsAsync(cancellationToken);
            var catalogIds = contents.Videos.Select(video => video.Id).ToHashSet();
            var added = new List<Guid>();
            var removed = snapshot.Videos.Keys.Where(id => !catalogIds.Contains(id)).ToArray();
            var builder = snapshot.Videos.ToBuilder();
            builder.RemoveRange(removed);
            foreach (var video in contents.Videos.Where(video => !snapshot.Videos.ContainsKey(video.Id)))
            {
                builder[video.Id] = FromCatalog(video, contents.Cache);
                added.Add(video.Id);
            }
            var videos = builder.ToImmutable();
            var albums = LibrarySnapshot.Sort(contents.Albums);
            var members = Memberships(contents, videos);
            var albumsChanged = !albums.SequenceEqual(snapshot.Albums) || !SameMembers(members, snapshot.AlbumMembers);
            if (added.Count == 0 && removed.Length == 0 && !albumsChanged) return (snapshot, (LibraryDelta?)null, true);
            var tags = snapshot.Tags.Apply(removed.Select(id => (snapshot.Videos.GetValueOrDefault(id), (VideoRecord?)null))
                .Concat(added.Select(id => ((VideoRecord?)null, videos.GetValueOrDefault(id)))));
            var next = snapshot.With(videos, albums, members, tags);
            return (next, new LibraryDelta(next, added, [], removed, albumsChanged, !ReferenceEquals(tags, snapshot.Tags)), true);
        }, cancellationToken);

    private static bool SameMembers(ImmutableDictionary<Guid, ImmutableHashSet<Guid>> left, ImmutableDictionary<Guid, ImmutableHashSet<Guid>> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && other.SetEquals(pair.Value));

    /// <summary>Stats every file and publishes missing/restored transitions; returns the records that need a read: those
    /// never read, and those whose file changed since the last read attempt (successful or not), or all when forced.</summary>
    private async Task<IReadOnlyList<VideoRecord>> StatAsync(bool force, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot;
        var stamps = await Task.Run(() => snapshot.Videos.Values.Select(record => (Record: record, Stamp: TryStat(record))).ToArray(), cancellationToken);
        var presence = new Dictionary<Guid, bool>();
        var toRead = new List<VideoRecord>();
        foreach (var (record, stamp) in stamps)
        {
            if (record.IsMissing != stamp is null) presence[record.Id] = stamp is null;
            if (stamp is not null && (force || record.State == VideoRecordState.Pending || record.Stamp != stamp)) toRead.Add(record);
        }
        if (presence.Count > 0)
            await MutateAsync(current =>
            {
                var builder = current.Videos.ToBuilder();
                var updated = new List<Guid>();
                foreach (var (id, missing) in presence)
                {
                    if (!builder.TryGetValue(id, out var record)) continue;
                    builder[id] = record with { IsMissing = missing };
                    updated.Add(id);
                }
                var next = current.With(builder.ToImmutable());
                return Task.FromResult<(LibrarySnapshot, LibraryDelta?, bool)>((next, new LibraryDelta(next, [], updated, [], false, false), true));
            }, cancellationToken);
        return toRead;
    }

    private FileStamp? TryStat(VideoRecord record)
    {
        try { return FileStamp.TryRead(GetVideoPath(record)); }
        catch (ArgumentException) { return null; }
    }

    private sealed record ReadOutcome(FileStamp? Stamp, VideoTextMetadata? Text, string? Error);

    private Task<ReadOutcome> ReadAsync(VideoRecord record, CancellationToken cancellationToken) =>
        ReadAsync(GetVideoPath(record), cancellationToken);

    /// <summary>Reads without holding the writer, so a slow file never blocks publishing or album edits.</summary>
    private async Task<ReadOutcome> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var stamp = FileStamp.TryRead(path);
        if (stamp is null) return new ReadOutcome(null, null, null);
        try { return new ReadOutcome(stamp, await _reader.ReadTextAsync(path, cancellationToken), null); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Could not read metadata from {path}: {exception}");
            return new ReadOutcome(stamp, null, exception.Message);
        }
    }

    /// <param name="Video">The catalog row for a video that may not be in the snapshot yet; null to update the snapshot's record.</param>
    private sealed record PendingCommit(Guid Id, LibraryVideo? Video, ReadOutcome Outcome);

    /// <param name="Unregistered">New videos that were dropped because they have no catalog row.</param>
    private sealed record CommitResult(int Applied, int Unregistered, IReadOnlyList<ReconcileError> Errors);

    /// <summary>Publishes read outcomes in one batch. A read records the stamp it saw, whether it succeeded or not, so an
    /// unreadable file is retried only once it changes. A successful read whose file changed since it started is stale:
    /// it is dropped (a newer writer publishes its own read), or with <paramref name="publishStaleAsPending"/> the video
    /// becomes Pending so the next stat pass reads it. A video joins the snapshot only while its catalog row exists, so a
    /// read that finishes after the video was removed cannot bring it back.</summary>
    private async Task<CommitResult> CommitAsync(IReadOnlyList<PendingCommit> outcomes, bool publishStaleAsPending)
    {
        var errors = new List<ReconcileError>();
        var unregistered = 0;
        // The reads are done; committing them is quick and must not be lost to a late cancellation.
        var applied = await MutateAsync(async snapshot =>
        {
            var registered = new HashSet<Guid>();
            foreach (var (id, _, _) in outcomes.Where(commit => commit.Video is not null && !snapshot.Videos.ContainsKey(commit.Id)))
            {
                if (await Catalog.ContainsVideoAsync(id, CancellationToken.None)) registered.Add(id);
                else unregistered++;
            }
            var builder = snapshot.Videos.ToBuilder();
            var writes = new List<CatalogCacheWrite>();
            var added = new List<Guid>();
            var updated = new List<Guid>();
            var tagChanges = new List<(VideoRecord?, VideoRecord?)>();
            foreach (var (id, video, outcome) in outcomes)
            {
                // Catalog fields come from the snapshot when present: a caller's LibraryVideo carries the effective
                // (embedded) recorded date, not the catalog's.
                var current = snapshot.Find(id);
                if ((current ?? (video is not null && registered.Contains(id) ? VideoRecord.FromCatalog(video) : null)) is not { } baseRecord) continue;
                VideoRecord next;
                if (outcome.Stamp is null)
                    next = baseRecord with { IsMissing = true };
                else if (outcome.Text is null)
                {
                    next = baseRecord with { State = VideoRecordState.Unreadable, Error = outcome.Error, Stamp = outcome.Stamp, IsMissing = false };
                    errors.Add(new ReconcileError(id, baseRecord.Label, outcome.Error ?? "Unreadable"));
                }
                else if (FileStamp.TryRead(GetVideoPath(baseRecord)) != outcome.Stamp)
                {
                    if (!publishStaleAsPending) continue;
                    next = baseRecord with { State = VideoRecordState.Pending, Stamp = null, Error = null };
                }
                else
                {
                    next = baseRecord.WithContent(outcome.Text, outcome.Stamp.Value);
                    writes.Add(new CatalogCacheWrite(id, new CatalogCacheEntry(next.ToCacheJson(), outcome.Stamp.Value.Length,
                        outcome.Stamp.Value.ModifiedTicks), next.Duration));
                }
                builder[id] = next;
                (current is null ? added : updated).Add(id);
                tagChanges.Add((current, next));
            }
            if (added.Count == 0 && updated.Count == 0) return (snapshot, null, 0);
            try { await Catalog.WriteCacheAsync(writes, CancellationToken.None); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The cache is rebuildable; the MKVs were read correctly, so publish them anyway.
                Trace.TraceWarning($"Could not save the metadata cache: {exception}");
                errors.Add(new ReconcileError(Guid.Empty, "Library cache", "Could not save the metadata cache: " + exception.Message));
            }
            var tags = snapshot.Tags.Apply(tagChanges);
            var result = snapshot.With(builder.ToImmutable(), tags: tags);
            return (result, new LibraryDelta(result, added, updated, [], false, !ReferenceEquals(tags, snapshot.Tags)), added.Count + updated.Count);
        }, CancellationToken.None);
        return new CommitResult(applied, unregistered, errors);
    }

    private static VideoRecord FromCatalog(LibraryVideo video, IReadOnlyDictionary<Guid, CatalogCacheEntry> cache)
    {
        var record = VideoRecord.FromCatalog(video) with { Title = "", Description = "" };
        return cache.TryGetValue(video.Id, out var entry) ? record.WithCachedContent(entry) ?? record : record;
    }

    private static ImmutableDictionary<Guid, ImmutableHashSet<Guid>> Memberships(CatalogContents contents, IReadOnlyDictionary<Guid, VideoRecord> videos)
    {
        var albums = contents.Albums.Select(album => album.Id).ToHashSet();
        var members = contents.Albums.ToDictionary(album => album.Id, _ => ImmutableHashSet.CreateBuilder<Guid>());
        foreach (var (albumId, videoId) in contents.Memberships)
            if (albums.Contains(albumId) && videos.ContainsKey(videoId)) members[albumId].Add(videoId);
        return members.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutable());
    }

    private async Task<T> MutateAsync<T>(Func<LibrarySnapshot, Task<(LibrarySnapshot Next, LibraryDelta? Delta, T Result)>> mutate,
        CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken);
        try
        {
            var (next, delta, result) = await mutate(Snapshot);
            // Writers run off the UI thread; building search text here keeps the first search after a change fast.
            if (delta is not null)
                LibrarySearch.Prepare(delta.IsReset ? next.Videos.Values : delta.Added.Concat(delta.Updated).Select(id => next.Videos[id]));
            Volatile.Write(ref _snapshot, next);
            if (delta is not null) Raise(delta);
            return result;
        }
        finally { _writer.Release(); }
    }

    private void Raise(LibraryDelta delta)
    {
        foreach (var handler in Changed?.GetInvocationList().Cast<Action<LibraryDelta>>() ?? [])
        {
            try { handler(delta); }
            catch (Exception exception) { Trace.TraceError($"A library change handler failed: {exception}"); }
        }
    }
}
