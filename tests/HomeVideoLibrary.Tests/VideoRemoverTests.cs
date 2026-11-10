using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class VideoRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-remover-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();

    private async Task<(LibraryModel Model, LibraryVideo Video, FakeTrash Trash)> OpenAsync()
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var video = await AddVideoAsync(catalog, "birthday");
        _files.Set(catalog.GetVideoPath(video.RelativePath), VideoMetadata.Empty with { Title = "Birthday" });
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        await model.ReconcileAsync();
        return (model, video, new FakeTrash(Path.Combine(_root, "Trash")));
    }

    [Fact]
    public async Task TrashingReleasesTheHoldersMovesTheFileThenForgetsTheVideoAndItsCaches()
    {
        var (model, video, trash) = await OpenAsync();
        var path = model.Catalog.GetVideoPath(video.RelativePath);
        var thumbnails = Directory.CreateDirectory(Path.Combine(model.Catalog.Paths.ThumbnailCacheDirectory, video.Id.ToString("N"))).FullName;
        var previews = Directory.CreateDirectory(Path.Combine(model.Catalog.Paths.PreviewDirectory, video.Id.ToString("N"))).FullName;
        var holders = new RecordingHolders();
        holders.OnRelease = () => Assert.True(File.Exists(path));

        var result = await new VideoRemover(model, trash, holders).MoveToTrashAsync(video.Id);

        Assert.Equal(new VideoRemoval(video.Id, "Birthday", RemovalResult.Trashed), result);
        Assert.Equal([path], trash.Moved);
        Assert.False(File.Exists(path));
        Assert.Null(model.Snapshot.Find(video.Id));
        Assert.Empty(await model.Catalog.ListAsync());
        Assert.False(Directory.Exists(thumbnails));
        Assert.False(Directory.Exists(previews));
        Assert.Equal(["release " + video.Id, "forget " + video.Id], holders.Calls);
    }

    [Fact]
    public async Task AFileTheTrashCannotTakeStaysInTheLibrary()
    {
        var (model, video, trash) = await OpenAsync();
        trash.Answers.Enqueue(TrashResult.Unavailable);

        var result = await new VideoRemover(model, trash).MoveToTrashAsync(video.Id);

        Assert.Equal(RemovalResult.Unavailable, result.Result);
        Assert.False(result.IsRemoved);
        Assert.NotNull(model.Snapshot.Find(video.Id));
        Assert.True(File.Exists(model.Catalog.GetVideoPath(video.RelativePath)));
    }

    [Fact]
    public async Task ALockedFileIsRetriedAndThenReportedWithItsVideoKept()
    {
        var (model, video, trash) = await OpenAsync();
        trash.Answers.Enqueue(TrashResult.Locked);
        trash.Answers.Enqueue(TrashResult.Locked);
        var remover = new VideoRemover(model, trash);

        Assert.Equal(RemovalResult.Trashed, (await remover.MoveToTrashAsync(video.Id)).Result);
        Assert.Equal(3, trash.Attempts);

        var (model2, video2, stubborn) = await OpenAgainAsync();
        stubborn.AlwaysLocked = true;
        Assert.Equal(RemovalResult.Locked, (await new VideoRemover(model2, stubborn).MoveToTrashAsync(video2.Id)).Result);
        Assert.Equal(4, stubborn.Attempts);
        Assert.NotNull(model2.Snapshot.Find(video2.Id));
    }

    private async Task<(LibraryModel, LibraryVideo, FakeTrash)> OpenAgainAsync()
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "second"));
        var video = await AddVideoAsync(catalog, "second");
        var model = new LibraryModel(catalog, _files);
        await model.LoadAsync();
        return (model, video, new FakeTrash(Path.Combine(_root, "Trash2")));
    }

    [Fact]
    public async Task ATrashThatFailsAfterMovingTheFileLeavesAMissingRowThatCanBeRemoved()
    {
        var (model, video, trash) = await OpenAsync();
        trash.FailAfterMoving = true;
        var remover = new VideoRemover(model, trash);

        var result = await remover.MoveToTrashAsync(video.Id);

        Assert.Equal(RemovalResult.Failed, result.Result);
        Assert.NotNull(model.Snapshot.Find(video.Id));
        await model.StatPassAsync();
        Assert.True(model.Snapshot.Find(video.Id)!.IsMissing);
        Assert.Equal(RemovalResult.Missing, (await remover.MoveToTrashAsync(video.Id)).Result);

        var removed = Assert.Single(await remover.RemoveMissingAsync([video.Id]));
        Assert.Equal(RemovalResult.RemovedFromLibrary, removed.Result);
        Assert.Empty(model.Snapshot.Videos);
        Assert.Empty(await model.Catalog.ListAsync());
    }

    [Fact]
    public async Task RemoveFromLibraryLeavesAVideoWhoseFileIsThere()
    {
        var (model, video, _) = await OpenAsync();

        var result = Assert.Single(await new VideoRemover(model, new FakeTrash(_root)).RemoveMissingAsync([video.Id]));

        Assert.Equal(RemovalResult.Failed, result.Result);
        Assert.NotNull(model.Snapshot.Find(video.Id));
    }

    [Fact]
    public async Task TheTrashWaitsForAMetadataOperationOnTheSameFile()
    {
        var (model, video, trash) = await OpenAsync();
        var lease = await MatroskaMetadataService.AcquireExclusiveAsync(model.Catalog.GetVideoPath(video.RelativePath));

        var trashing = new VideoRemover(model, trash).MoveToTrashAsync(video.Id);
        await Task.Delay(200);
        Assert.False(trashing.IsCompleted);
        Assert.Empty(trash.Moved);

        lease.Dispose();
        Assert.Equal(RemovalResult.Trashed, (await trashing).Result);
    }

    [Fact]
    public async Task AFileTheSystemDeletedOutrightIsReportedDeletedNotTrashed()
    {
        var (model, video, trash) = await OpenAsync();
        trash.Answers.Enqueue(TrashResult.Deleted);

        Assert.Equal(RemovalResult.Deleted, (await new VideoRemover(model, trash).MoveToTrashAsync(video.Id)).Result);

        Assert.Null(model.Snapshot.Find(video.Id));
    }

    [Fact]
    public async Task DeletingPermanentlyRemovesTheFileAndTheVideo()
    {
        var (model, video, _) = await OpenAsync();
        var path = model.Catalog.GetVideoPath(video.RelativePath);

        Assert.Equal(RemovalResult.Deleted, (await new VideoRemover(model, new FakeTrash(_root)).DeletePermanentlyAsync(video.Id)).Result);

        Assert.False(File.Exists(path));
        Assert.Null(model.Snapshot.Find(video.Id));
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }

    private sealed class RecordingHolders : IVideoHolders
    {
        public List<string> Calls { get; } = [];
        public Action? OnRelease { get; set; }

        public Task ReleaseAsync(Guid videoId, CancellationToken cancellationToken)
        {
            OnRelease?.Invoke();
            Calls.Add("release " + videoId);
            return Task.CompletedTask;
        }

        public void Forget(Guid videoId) => Calls.Add("forget " + videoId);
    }
}

/// <summary>A Trash that moves files into a test directory, or answers from a queue without touching them.</summary>
internal sealed class FakeTrash(string directory) : IFileTrash
{
    private int _attempts;
    public Queue<TrashResult> Answers { get; } = new();
    public Dictionary<string, TrashResult> ByName { get; } = [];
    public List<string> Moved { get; } = [];
    public bool AlwaysLocked { get; set; }
    public bool FailAfterMoving { get; set; }
    public int Attempts => _attempts;

    public TrashResult MoveToTrash(string path)
    {
        Interlocked.Increment(ref _attempts);
        FileTrash.RequireFile(path);
        if (AlwaysLocked) return TrashResult.Locked;
        if (Answers.TryDequeue(out var answer) && answer != TrashResult.Trashed)
        {
            if (answer == TrashResult.Deleted) File.Delete(path);
            return answer;
        }
        if (ByName.TryGetValue(Path.GetFileName(path), out var named) && named != TrashResult.Trashed) return named;
        Directory.CreateDirectory(directory);
        File.Move(path, Path.Combine(directory, Path.GetFileName(path)));
        lock (Moved) Moved.Add(path);
        if (FailAfterMoving) throw new IOException("The process was interrupted.");
        return TrashResult.Trashed;
    }
}
