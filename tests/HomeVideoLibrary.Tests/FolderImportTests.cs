using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class FolderImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-folder-import-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();

    private string Sources(params string[] names)
    {
        var folder = Path.Combine(_root, "sources");
        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, name);
        }
        return folder;
    }

    [Fact]
    public Task RecursiveFolderUsesIndividualJobsContinuesAfterFailureAndSkipsKnownSourcesOnRepeat() => UiThread.Run(async () =>
    {
        var folder = Sources("one.mp4", "nested/two.mov", "broken.avi", "notes.txt");
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var model = new LibraryModel(catalog, _files);
        var importer = new RememberingImporter(catalog, _files);
        var queue = new JobQueue(_ => Task.CompletedTask);
        var vm = new ImportViewModel(() => model, _ => importer, new FakeInteraction(), queue, () => true, _ => { });

        await vm.EnqueueFolder(folder)!.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await queue.WhenIdleAsync();
        Assert.Equal(3, queue.Jobs.Count);
        Assert.Equal(2, queue.Jobs.Count(job => job.State == JobState.Succeeded));
        Assert.Equal("bad header", Assert.Single(queue.Jobs, job => job.HasFailed).Error);
        Assert.Equal(2, model.Snapshot.Videos.Count);
        Assert.Contains("1 skipped", vm.FolderScanSummary);
        Assert.Equal("one.mp4", File.ReadAllText(Path.Combine(folder, "one.mp4")));

        queue.ClearFinishedCommand.Execute(null);
        await vm.EnqueueFolder(folder)!.Completion;
        await queue.WhenIdleAsync();
        Assert.Equal(2, queue.Jobs.Count(job => job.StatusText.StartsWith("Already in the library.")));
        Assert.Equal(2, model.Snapshot.Videos.Count);
        Assert.Equal(6, importer.Attempts);
    });

    [Fact]
    public Task CancellingDiscoveryQueuesNothingAndDoesNotBlockTheUiThread() => UiThread.Run(async () =>
    {
        var folder = Sources("one.mp4");
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var model = new LibraryModel(catalog, _files);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var discovery = new FolderVideoDiscovery(path =>
        {
            Assert.NotEqual(uiThread, Environment.CurrentManagedThreadId);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return Directory.EnumerateFileSystemEntries(path);
        });
        var queue = new JobQueue(_ => Task.CompletedTask);
        var vm = new ImportViewModel(() => model, _ => throw new InvalidOperationException("must not import"), new FakeInteraction(),
            queue, () => true, _ => { }, discovery);
        var scan = vm.EnqueueFolder(folder)!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        scan.Cancel();
        release.Set();
        await scan.Completion;
        Assert.Equal(JobState.Cancelled, scan.State);
        Assert.Single(queue.Jobs);
        Assert.Contains("no videos", vm.FolderScanSummary);
    });

    [Fact]
    public Task CancellingOneQueuedVideoLeavesTheOtherPerVideoJobsAlone() => UiThread.Run(async () =>
    {
        var folder = Sources("one.mp4", "two.mp4", "three.mp4");
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var model = new LibraryModel(catalog, _files);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = new RememberingImporter(catalog, _files) { BeforeImport = async () => { started.TrySetResult(); await release.Task; } };
        var queue = new JobQueue(_ => Task.CompletedTask);
        var vm = new ImportViewModel(() => model, _ => importer, new FakeInteraction(), queue, () => true, _ => { });
        await vm.EnqueueFolder(folder)!.Completion;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = queue.Jobs.First(job => job.IsQueued);
        cancelled.Cancel();
        release.SetResult();
        await queue.WhenIdleAsync();
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Equal(2, model.Snapshot.Videos.Count);
    });

    [Fact]
    public Task ChangingLibraryWhilePickerIsOpenDoesNotImportIntoEitherLibrary() => UiThread.Run(async () =>
    {
        var folder = Sources("one.mp4");
        var first = new LibraryModel(await CreateCatalogAsync(Path.Combine(_root, "first")), _files);
        var second = new LibraryModel(await CreateCatalogAsync(Path.Combine(_root, "second")), _files);
        var current = first;
        var picker = new PendingPicker();
        var queue = new JobQueue(_ => Task.CompletedTask);
        var vm = new ImportViewModel(() => current, _ => throw new InvalidOperationException("must not import"), picker, queue, () => true, _ => { });
        var picking = vm.ImportFolderCommand.ExecuteAsync();
        current = second;
        picker.Folder.SetResult(folder);
        await picking;
        Assert.Empty(queue.Jobs);
    });

    [Fact]
    public Task RefusingToQuitWithAQueuedScanStillImportsAndRequiresALaterExplicitQuit() => UiThread.Run(async () =>
    {
        var folder = Sources("one.mp4", "nested/two.mp4");
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new FakeInteraction();
        var importer = new RememberingImporter(catalog, _files);
        var vm = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = interaction,
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, new UnusedMetadata()),
            CreateImporter = _ => importer,
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            Post = action => action(),
        });
        await vm.OpenLibraryAsync(catalog.Paths.RootDirectory);
        vm.Jobs.Enqueue("Earlier import", JobLane.Import, async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return JobOutcome.Done("Done");
        });
        await started.Task;
        var scan = vm.Import.EnqueueFolder(folder)!;
        Assert.False(await vm.RequestCloseAsync());
        Assert.Contains("Activity", Assert.Single(interaction.Errors));
        release.SetResult();
        await scan.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await vm.Jobs.WhenIdleAsync();
        Assert.Equal(2, importer.Attempts);
        Assert.False(vm.Jobs.HasActive);
        Assert.NotNull(vm.Model);
        Assert.True(await vm.RequestCloseAsync());
        Assert.Equal(2, (await catalog.ListAsync()).Count);
    });

    private sealed class RememberingImporter(LibraryCatalog catalog, FakeMatroska files) : IVideoImporter
    {
        private readonly Dictionary<string, LibraryVideo> _known = [];
        public int Attempts { get; private set; }
        public Func<Task>? BeforeImport { get; init; }

        public async Task<ImportResult> ImportAsync(string sourcePath, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (BeforeImport is not null) await BeforeImport();
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(sourcePath);
            if (name == "broken.avi") throw new InvalidDataException("bad header");
            if (_known.TryGetValue(name, out var known)) return new(ImportOutcome.AlreadyInLibrary, known, []);
            var video = await AddVideoAsync(catalog, name);
            files.Set(catalog.GetVideoPath(video.RelativePath), VideoMetadata.Empty with { Title = name });
            _known.Add(name, video);
            return new(ImportOutcome.Imported, video, []);
        }
    }

    private sealed class PendingPicker : IInteraction
    {
        public TaskCompletionSource<string?> Folder { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> PickFolderAsync(string title) => Folder.Task;
        public Task<PickedFiles> PickVideosAsync() => Task.FromResult(PickedFiles.None);
        public Task<LeaveChoice> AskLeaveAsync(string title, string? cannotSave) => Task.FromResult(LeaveChoice.KeepEditing);
        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
        public Task<bool> ConfirmAsync(Confirmation confirmation) => Task.FromResult(false);
        public Task ShowSettingsAsync(SettingsViewModel settings) => Task.CompletedTask;
    }

    private sealed class UnusedMetadata : IVideoMetadataService
    {
        public Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
