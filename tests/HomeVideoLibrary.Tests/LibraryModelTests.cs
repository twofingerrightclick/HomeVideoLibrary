using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class LibraryModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-model-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();

    [Fact]
    public async Task LoadPublishesTheCachedLibraryBeforeAnyReadAndNewRowsArePending()
    {
        var catalog = await CreateCatalogAsync(_root);
        var cached = await AddVideoAsync(catalog, "cached.mp4");
        _files.Set(catalog.GetVideoPath(cached.RelativePath), VideoMetadata.Empty with { Title = "Lake day", Tags = ["summer"] });
        await new LibraryModel(catalog, _files).ReconcileAsync();
        var uncached = await AddVideoAsync(catalog, "grandma visit.mov");
        var reads = _files.Reads;

        var model = new LibraryModel(catalog, _files);
        var deltas = new List<LibraryDelta>();
        model.Changed += deltas.Add;
        var load = await model.LoadAsync();

        Assert.Equal(reads, _files.Reads);
        Assert.Equal((2, 1), (load.Videos, load.Pending));
        Assert.True(Assert.Single(deltas).IsReset);
        Assert.Equal("Lake day", model.Snapshot.Find(cached.Id)!.Title);
        Assert.Equal(VideoRecordState.Pending, model.Snapshot.Find(uncached.Id)!.State);
        Assert.Equal(1, model.Snapshot.Tags.Find("summer")!.VideoCount);
        Assert.Equal(cached.Id, Assert.Single(LibrarySearch.Run(model.Snapshot, "lake")).Video.Id);
        Assert.Equal(uncached.Id, Assert.Single(LibrarySearch.Run(model.Snapshot, "grandma")).Video.Id);
    }

    [Fact]
    public async Task ReconcileReadsOnlyNewOrChangedFilesAndRefreshesDuration()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "first");
        var second = await AddVideoAsync(catalog, "second");
        _files.Set(catalog.GetVideoPath(first.RelativePath), VideoMetadata.Empty with { Description = "original" }, TimeSpan.FromSeconds(42));
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();

        var result = await model.ReconcileAsync();
        Assert.Equal((2, 2, 0), (result.Total, result.Read, result.Missing));
        Assert.Equal(TimeSpan.FromSeconds(42), model.Snapshot.Find(first.Id)!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(42), (await catalog.ListAsync()).Single(video => video.Id == first.Id).Duration);

        Assert.Equal(0, (await model.ReconcileAsync()).Read);
        Assert.Equal(2, _files.Reads);

        _files.Set(catalog.GetVideoPath(first.RelativePath), VideoMetadata.Empty with { Description = "edited elsewhere" });
        await File.AppendAllTextAsync(catalog.GetVideoPath(first.RelativePath), " changed");
        Assert.Equal(1, await model.StatPassAsync());
        Assert.Equal(1, (await model.ReconcileAsync()).Read);
        Assert.Equal("edited elsewhere", model.Snapshot.Find(first.Id)!.Description);
        Assert.Equal(2, (await model.ReconcileAsync(force: true)).Read);
    }

    [Fact]
    public async Task DeletedFileBecomesMissingKeepsItsContentAndIsRestoredWithoutARead()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        var path = catalog.GetVideoPath(video.RelativePath);
        _files.Set(path, VideoMetadata.Empty with { Title = "Wedding" });
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        await model.ReconcileAsync();
        var stamp = model.Snapshot.Find(video.Id)!.Stamp!.Value;
        var bytes = await File.ReadAllBytesAsync(path);

        File.Delete(path);
        var result = await model.ReconcileAsync();
        Assert.Equal(1, result.Missing);
        Assert.Empty(result.Errors);
        var missing = model.Snapshot.Find(video.Id)!;
        Assert.True(missing.IsMissing);
        Assert.Equal(VideoRecordState.Ok, missing.State);
        Assert.Equal(video.Id, Assert.Single(LibrarySearch.Run(model.Snapshot, "wedding")).Video.Id);

        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, new DateTime(stamp.ModifiedTicks, DateTimeKind.Utc));
        var reads = _files.Reads;
        Assert.Equal(0, await model.StatPassAsync());
        Assert.False(model.Snapshot.Find(video.Id)!.IsMissing);
        Assert.Equal(reads, _files.Reads);
    }

    [Fact]
    public async Task UnreadableFilesAreCollectedAndRetried()
    {
        var catalog = await CreateCatalogAsync(_root);
        var good = await AddVideoAsync(catalog, "good");
        var bad = await AddVideoAsync(catalog, "bad");
        _files.Failures[catalog.GetVideoPath(bad.RelativePath)] = "mkvmerge failed";
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();

        var result = await model.ReconcileAsync();
        Assert.Equal(bad.Id, Assert.Single(result.Errors).VideoId);
        Assert.Equal(VideoRecordState.Unreadable, model.Snapshot.Find(bad.Id)!.State);
        Assert.Equal(VideoRecordState.Ok, model.Snapshot.Find(good.Id)!.State);

        // An unchanged unreadable file is not read again on every refresh or window activation.
        Assert.Equal(0, await model.StatPassAsync());
        Assert.Equal(0, (await model.ReconcileAsync()).Read);
        Assert.Equal(2, _files.Reads);

        _files.Failures.Clear();
        await File.AppendAllTextAsync(catalog.GetVideoPath(bad.RelativePath), " repaired");
        Assert.Equal(1, (await model.ReconcileAsync()).Read);
        Assert.Equal(VideoRecordState.Ok, model.Snapshot.Find(bad.Id)!.State);
        Assert.Equal(2, (await model.ReconcileAsync(force: true)).Read);
    }

    [Fact]
    public async Task ImportsAndAlbumsRacingAReconcileAreNeverReverted()
    {
        var catalog = await CreateCatalogAsync(_root);
        await AddVideoAsync(catalog, "existing");
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        for (var round = 0; round < 60; round++)
        {
            var reconcile = Task.Run(() => model.ReconcileAsync());
            var imported = await AddVideoAsync(catalog, $"import {round}");
            await model.UpsertFromFileAsync(imported);
            var album = await model.CreateAlbumAsync($"Album {round}", [imported.Id]);
            await reconcile;
            Assert.NotNull(model.Snapshot.Find(imported.Id));
            Assert.Contains(imported.Id, model.Snapshot.MembersOf(album.Id));
        }
    }

    [Fact]
    public async Task RemovingVideosDeletesTheirRowsMembershipsAndCacheAndPublishesOneDelta()
    {
        var catalog = await CreateCatalogAsync(_root);
        var removed = await AddVideoAsync(catalog, "removed");
        var kept = await AddVideoAsync(catalog, "kept");
        _files.Set(catalog.GetVideoPath(removed.RelativePath), VideoMetadata.Empty with { Tags = ["beach", "shared"] });
        _files.Set(catalog.GetVideoPath(kept.RelativePath), VideoMetadata.Empty with { Tags = ["shared"] });
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        await model.ReconcileAsync();
        var album = await model.CreateAlbumAsync("Summer", [removed.Id, kept.Id]);
        var deltas = new List<LibraryDelta>();
        model.Changed += deltas.Add;

        await model.RemoveVideosAsync([removed.Id, Guid.NewGuid()]);

        var delta = Assert.Single(deltas);
        Assert.Equal([removed.Id], delta.Removed);
        Assert.True(delta.AlbumsChanged);
        Assert.True(delta.TagsChanged);
        Assert.Null(model.Snapshot.Find(removed.Id));
        Assert.Equal([kept.Id], model.Snapshot.MembersOf(album.Id));
        Assert.Null(model.Snapshot.Tags.Find("beach"));
        Assert.Equal(1, model.Snapshot.Tags.Find("shared")!.VideoCount);
        Assert.True(File.Exists(catalog.GetVideoPath(removed.RelativePath)));
        var contents = await catalog.LoadContentsAsync();
        Assert.Equal([kept.Id], contents.Videos.Select(video => video.Id));
        Assert.Equal([(album.Id, kept.Id)], contents.Memberships);
        Assert.Equal([kept.Id], contents.Cache.Keys);
        Assert.Equal(1, await CountRowsAsync(catalog, "video_cache"));

        var reloaded = new LibraryModel(catalog, _files);
        await reloaded.LoadAsync();
        Assert.Null(reloaded.Snapshot.Find(removed.Id));
    }

    [Fact]
    public async Task ASaveFinishingAfterItsVideoWasRemovedDoesNotBringItBack()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "trashed");
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        await model.RemoveVideosAsync([video.Id]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => model.UpsertFromFileAsync(video));
        await model.ReconcileAsync();

        Assert.Null(model.Snapshot.Find(video.Id));
    }

    [Fact]
    public async Task AVideoWhoseFileWasTrashedButWhoseRowSurvivedACrashIsMissingAndCanBeRemoved()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "half removed");
        var trashed = Path.Combine(_root, "Trash", "half removed.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(trashed)!);
        File.Move(catalog.GetVideoPath(video.RelativePath), trashed);

        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        await model.ReconcileAsync();
        Assert.True(model.Snapshot.Find(video.Id)!.IsMissing);

        await model.RemoveVideosAsync([video.Id]);
        Assert.Empty(model.Snapshot.Videos);
        Assert.Empty(await catalog.ListAsync());
        Assert.True(File.Exists(trashed));
    }

    private static async Task<long> CountRowsAsync(LibraryCatalog catalog, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AFileThatKeepsChangingWhileReadIsPublishedAsPendingNotAsStaleContent()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "busy file");
        var path = catalog.GetVideoPath(video.RelativePath);
        _files.Set(path, VideoMetadata.Empty with { Title = "stale" });
        _files.BeforeRead = async (_, _) => await File.AppendAllTextAsync(path, " changed");
        var model = new LibraryModel(catalog, _files);

        var record = await model.UpsertFromFileAsync(video);

        Assert.Equal(VideoRecordState.Pending, record.State);
        Assert.Null(record.Stamp);
        Assert.Equal("", record.Title);
        _files.BeforeRead = null;
        Assert.Equal(1, await model.StatPassAsync());
    }

    [Fact]
    public async Task ReconcilePublishesOneDeltaPerBatchAndPicksUpNewCatalogRows()
    {
        var catalog = await CreateCatalogAsync(_root);
        for (var index = 0; index < 30; index++) await AddVideoAsync(catalog, $"video {index}");
        var model = new LibraryModel(catalog, _files, readParallelism: 3);
        await model.LoadAsync();
        var late = await AddVideoAsync(catalog, "added by another window");
        var deltas = new List<LibraryDelta>();
        model.Changed += deltas.Add;

        await model.ReconcileAsync();

        Assert.Equal([late.Id], deltas[0].Added);
        Assert.Equal([25, 6], deltas.Skip(1).Select(delta => delta.Updated.Count));
        Assert.All(model.Snapshot.Videos.Values, record => Assert.Equal(VideoRecordState.Ok, record.State));
    }

    [Fact]
    public async Task CancelledReconcileKeepsFinishedBatches()
    {
        var catalog = await CreateCatalogAsync(_root);
        for (var index = 0; index < 30; index++) await AddVideoAsync(catalog, $"video {index}");
        var model = new LibraryModel(catalog, _files, readParallelism: 1);
        await model.LoadAsync();
        using var cancellation = new CancellationTokenSource();
        _files.BeforeRead = (_, token) =>
        {
            if (_files.Reads == 27) cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ReconcileAsync(cancellationToken: cancellation.Token));

        Assert.Equal(25, model.Snapshot.Videos.Values.Count(record => record.State == VideoRecordState.Ok));
        var reloaded = new LibraryModel(catalog, _files);
        Assert.Equal(5, (await reloaded.LoadAsync()).Pending);
    }

    [Fact]
    public async Task UpsertAddsAnImportedVideoAndAnOlderBackgroundReadCannotOverwriteANewerSave()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        var path = catalog.GetVideoPath(video.RelativePath);
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        var imported = await AddVideoAsync(catalog, "imported");
        _files.Set(catalog.GetVideoPath(imported.RelativePath), VideoMetadata.Empty with { Tags = ["new tag"] });
        var deltas = new List<LibraryDelta>();
        model.Changed += deltas.Add;

        var record = await model.UpsertFromFileAsync(imported);
        Assert.Same(record, model.Snapshot.Find(imported.Id));
        Assert.Equal([imported.Id], Assert.Single(deltas).Added);
        Assert.True(deltas[0].TagsChanged);
        Assert.NotNull(model.Snapshot.Tags.Find("new tag"));

        _files.Set(path, VideoMetadata.Empty with { Description = "older snapshot" });
        var oldReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _files.BeforeRead = async (_, token) =>
        {
            if (oldReadStarted.TrySetResult()) await releaseOldRead.Task.WaitAsync(token);
        };
        var background = Task.Run(() => model.ReconcileAsync());
        await oldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A save rewrites the file and publishes its own read while the background read still holds the old content.
        _files.Set(path, VideoMetadata.Empty with { Description = "newer saved metadata" });
        await File.AppendAllTextAsync(path, " saved");
        var saved = await model.UpsertFromFileAsync(model.Snapshot.Find(video.Id)!.ToLibraryVideo());
        Assert.Equal("newer saved metadata", saved.Description);
        releaseOldRead.SetResult();
        await background.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("newer saved metadata", model.Snapshot.Find(video.Id)!.Description);
        Assert.Empty(LibrarySearch.Run(model.Snapshot, "older"));
    }

    [Fact]
    public async Task UpsertKeepsTheCatalogRecordedDateWhenTheEmbeddedDateIsCleared()
    {
        var catalog = await CreateCatalogAsync(_root);
        var imported = new DateTimeOffset(2001, 2, 3, 0, 0, 0, TimeSpan.Zero);
        var video = await AddVideoAsync(catalog, "video", imported);
        var path = catalog.GetVideoPath(video.RelativePath);
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        _files.Set(path, VideoMetadata.Empty with { RecordedAt = new DateTimeOffset(1997, 7, 3, 12, 0, 0, TimeSpan.Zero) });
        var dated = await model.UpsertFromFileAsync(video);
        Assert.Equal(1997, dated.RecordedAt.Year);

        _files.Set(path, VideoMetadata.Empty);
        await File.AppendAllTextAsync(path, " cleared");
        Assert.Equal(imported, (await model.UpsertFromFileAsync(dated.ToLibraryVideo())).RecordedAt);
    }

    [Fact]
    public async Task UpsertOfAMissingFileDoesNotThrow()
    {
        var catalog = await CreateCatalogAsync(_root);
        var video = await AddVideoAsync(catalog, "video");
        File.Delete(catalog.GetVideoPath(video.RelativePath));
        var model = new LibraryModel(catalog, _files);
        Assert.True((await model.UpsertFromFileAsync(video)).IsMissing);
    }

    [Fact]
    public async Task AlbumOperationsUpdateTheSnapshotAndCountChanges()
    {
        var catalog = await CreateCatalogAsync(_root);
        var first = await AddVideoAsync(catalog, "first");
        var second = await AddVideoAsync(catalog, "second");
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        var deltas = new List<LibraryDelta>();
        model.Changed += deltas.Add;

        var album = await model.CreateAlbumAsync(" Zoo ", [first.Id]);
        Assert.Equal("Zoo", album.Name);
        var added = await model.AddToAlbumAsync(album.Id, [first.Id, second.Id]);
        Assert.Equal((1, 1), (added.Changed, added.Unchanged));
        Assert.Equal(2, model.Snapshot.MembersOf(album.Id).Count);
        Assert.Equal(1, (await model.RemoveFromAlbumAsync(album.Id, [second.Id, second.Id])).Changed);
        await model.CreateAlbumAsync("Attic", []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => model.RenameAlbumAsync(album.Id, "attic"));
        var renamed = await model.RenameAlbumAsync(album.Id, "Aquarium");
        Assert.Equal(["Aquarium", "Attic"], model.Snapshot.Albums.Select(item => item.Name));
        Assert.Equal(renamed, model.Snapshot.AlbumsContaining(first.Id).Single());
        await model.DeleteAlbumAsync(album.Id);
        Assert.Empty(model.Snapshot.MembersOf(album.Id));
        Assert.All(deltas, delta => Assert.True(delta.AlbumsChanged));
        Assert.Equal(6, deltas.Count);

        var reloaded = new LibraryModel(catalog, _files);
        await reloaded.LoadAsync();
        Assert.Equal(["Attic"], reloaded.Snapshot.Albums.Select(item => item.Name));
    }

    [Fact]
    public async Task RealMkvMetadataIsReadIntoTheModelAndTheCacheRebuildsFromTheMkvAlone()
    {
        var catalog = await CreateCatalogAsync(_root);
        var tools = MediaTools.Discover();
        var id = Guid.NewGuid();
        var video = new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), "actual", TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var generated = await new MediaProcess().RunAsync(tools.Ffmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", catalog.GetVideoPath(video.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.Equal(0, generated.ExitCode);
        await catalog.AddAsync(video);
        var metadata = new MatroskaMetadataService(catalog, tools);
        await metadata.SaveAsync(video, VideoMetadata.Empty with
        {
            Description = "inside application", Tags = ["Picnic"],
            Chapters = [new VideoChapter(10, TimeSpan.FromSeconds(0.5), "New chapter", "A searchable moment", ["newtag"])],
        });
        var model = new LibraryModel(catalog, metadata);
        await model.LoadAsync();
        await model.ReconcileAsync();

        var record = model.Snapshot.Find(id)!;
        Assert.Equal(VideoRecordState.Ok, record.State);
        var chapter = Assert.Single(record.Chapters);
        Assert.Equal((TimeSpan.FromSeconds(0.5), record.Duration), (chapter.Start, chapter.End));
        Assert.InRange(record.Duration.TotalSeconds, 1.9, 2.1);
        Assert.Equal(chapter, Assert.Single(LibrarySearch.Run(model.Snapshot, "searchable")).Chapter);
        Assert.Equal(["Picnic", "newtag"], model.Snapshot.Tags.All().Select(tag => tag.Tag).Order(StringComparer.Ordinal));

        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM video_cache"; await command.ExecuteNonQueryAsync();
        }
        var rebuilt = new LibraryModel(catalog, metadata);
        Assert.Equal(1, (await rebuilt.LoadAsync()).Pending);
        Assert.Empty(LibrarySearch.Run(rebuilt.Snapshot, "searchable"));
        await rebuilt.ReconcileAsync();
        Assert.Single(LibrarySearch.Run(rebuilt.Snapshot, "searchable"));
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
