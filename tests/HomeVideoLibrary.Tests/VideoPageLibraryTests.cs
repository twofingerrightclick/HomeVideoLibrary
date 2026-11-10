using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.ViewModels.VideoPage;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

/// <summary>Video pages opened through the shell over a real library model.</summary>
public sealed class VideoPageLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-video-page-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _files = new();

    [Fact]
    public async Task ATagSavedOnOneVideoIsSuggestedOnTheNext()
    {
        var catalog = await CreateCatalogAsync(Path.Combine(_root, "library"));
        var first = await AddVideoAsync(catalog, "first");
        var second = await AddVideoAsync(catalog, "second");
        var shell = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = new FakeInteraction(),
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            CreateServices = _ => new LibraryServices(_files, new FileBackedMetadata(_files, catalog)),
            FindUnavailableTools = _ => Task.FromResult<IReadOnlyList<string>>([]),
            SweepScratch = (_, _) => Task.CompletedTask,
            CreatePlayback = () => new FakePlayback(),
            Post = action => action(),
        });
        await shell.OpenLibraryAsync(catalog.Paths.RootDirectory);
        await shell.Settings.RefreshIndexAsync();

        await shell.OpenVideoAsync(shell.Model!.Snapshot.Find(first.Id)!);
        var page = Assert.IsType<VideoPageViewModel>(shell.Page);
        page.Details.Tags.AddTagCommand.Execute("Lake Tahoe");
        await page.Details.SaveCommand.ExecuteAsync();
        Assert.True(await shell.NavigateAsync(() => null));
        await shell.OpenVideoAsync(shell.Model.Snapshot.Find(second.Id)!);
        var next = Assert.IsType<VideoPageViewModel>(shell.Page);

        Assert.Contains(next.Details.Tags.Suggest("lak", [], 5), suggestion => suggestion.Tag == "Lake Tahoe");
        Assert.Contains("Lake Tahoe", next.Details.Tags.Popular);
        Assert.True(await shell.RequestCloseAsync());
    }

    /// <summary>Saves into the fake MKV content the library model reads.</summary>
    private sealed class FileBackedMetadata(FakeMatroska files, LibraryCatalog catalog) : IVideoMetadataService
    {
        public async Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default) =>
            (await files.ReadTextAsync(catalog.GetVideoPath(video.RelativePath), cancellationToken)).Metadata;

        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default)
        {
            files.Set(catalog.GetVideoPath(video.RelativePath), MetadataText.AsStored(metadata));
            return Task.CompletedTask;
        }

        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_files.OutputDirectory);
    }
}
