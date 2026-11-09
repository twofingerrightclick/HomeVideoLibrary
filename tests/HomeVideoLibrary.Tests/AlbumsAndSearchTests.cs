using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class AlbumsAndSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-search-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AlbumMembershipIsIdempotentExcludesMembersAndNeverDeletesMedia()
    {
        var catalog = await CreateCatalogAsync();
        var first = await AddVideoAsync(catalog, "first"); var second = await AddVideoAsync(catalog, "second");
        var albums = new AlbumRepository(catalog);
        var album = await albums.CreateAsync(" Family ");
        await albums.AddVideosAsync(album.Id, [first.Id, first.Id]);
        await albums.AddVideosAsync(album.Id, [first.Id]);
        Assert.Equal(second.Id, Assert.Single(await albums.ListNonMembersAsync(album.Id)).Id);
        await albums.RenameAsync(album.Id, "Family films");
        Assert.Equal("Family films", Assert.Single(await albums.ListAsync()).Name);
        var index = new LibrarySearchIndex(catalog, new FakeMetadata());
        Assert.Equal(first.Id, Assert.Single(await index.SearchAsync("", album.Id)).Id);
        Assert.Equal(second.Id, Assert.Single(await index.SearchAsync("", album.Id, excludeAlbumMembers: true)).Id);
        await albums.RemoveVideosAsync(album.Id, [first.Id]);
        await albums.RemoveVideosAsync(album.Id, [first.Id]);
        Assert.Equal(2, (await albums.ListNonMembersAsync(album.Id)).Count);
        await albums.AddVideosAsync(album.Id, [first.Id]);
        await albums.DeleteAsync(album.Id);
        Assert.Empty(await albums.ListAsync());
        Assert.Equal(2, (await catalog.ListAsync()).Count);
        Assert.Equal("source first", await File.ReadAllTextAsync(catalog.GetVideoPath(first.RelativePath)));
        Assert.Equal("source second", await File.ReadAllTextAsync(catalog.GetVideoPath(second.RelativePath)));
        await Assert.ThrowsAsync<SqliteException>(() => albums.AddVideosAsync(album.Id, [first.Id]));
        var fromSelection = await albums.CreateAsync("From selection", [first.Id, second.Id, first.Id]);
        Assert.Empty(await albums.ListNonMembersAsync(fromSelection.Id));
        await Assert.ThrowsAsync<SqliteException>(() => albums.CreateAsync("Invalid selection", [Guid.NewGuid()]));
        Assert.Equal(fromSelection.Id, Assert.Single(await albums.ListAsync()).Id);
    }

    [Fact]
    public async Task SearchesUnicodePrefixesChapterDescriptionsAndTagsWithSafeUserSyntax()
    {
        var catalog = await CreateCatalogAsync();
        var video = await AddVideoAsync(catalog, "filename");
        var content = new FakeMetadata { Content = new VideoMetadata("Été à Montréal", "祖母生日 family holiday", ["réunion"],
            new DateTimeOffset(1997, 7, 3, 0, 0, 0, TimeSpan.Zero),
            [new VideoChapter(1, TimeSpan.Zero, "Candles", "Grandmother blows the birthday candles", ["cake-time"])], []) };
        var index = new LibrarySearchIndex(catalog, content);
        await index.RefreshAsync();
        foreach (var query in new[] { "été", "Mont", "祖母", "réun", "Grandmother", "cake", "\"Candles\" OR" })
        {
            var results = await index.SearchAsync(query);
            if (query.EndsWith(" OR", StringComparison.Ordinal)) Assert.Empty(results); // OR is literal text, never an operator.
            else Assert.Equal(video.Id, Assert.Single(results).Id);
        }
        foreach (var query in new[] { "\"", "() * : NEAR(", "' OR 1=1; DROP TABLE videos; --", "\u0301" })
            _ = await index.SearchAsync(query); // Punctuation and operators cannot become executable query syntax.
        var result = Assert.Single(await index.SearchAsync(""));
        Assert.Equal("Été à Montréal", result.Title);
        Assert.Equal("祖母生日 family holiday", result.Label);
        Assert.Equal(1997, result.RecordedAt.Year);
        Assert.Single(await catalog.ListAsync());
    }

    [Fact]
    public async Task ClearingEmbeddedDateUsesOriginalCatalogDateRatherThanAnOldCachedProjection()
    {
        var catalog = await CreateCatalogAsync();
        var video = await AddVideoAsync(catalog, "recorded date");
        var content = new FakeMetadata { Content = VideoMetadata.Empty with { RecordedAt = DateTimeOffset.Parse("1997-07-03T12:00:00Z") } };
        var index = new LibrarySearchIndex(catalog, content);
        await index.RefreshAsync();
        var projected = Assert.Single(await index.SearchAsync(""));
        Assert.Equal(1997, projected.RecordedAt.Year);
        content.Content = VideoMetadata.Empty;
        await index.UpdateVideoAsync(projected);
        Assert.Equal(video.RecordedAt, Assert.Single(await index.SearchAsync("")).RecordedAt);
    }

    [Fact]
    public async Task CancelledRefreshPreservesPreviouslySearchableCacheAndRetriesChangedFile()
    {
        var catalog = await CreateCatalogAsync();
        var video = await AddVideoAsync(catalog, "video");
        var content = new FakeMetadata { Content = VideoMetadata.Empty with { Description = "original searchable" } };
        var index = new LibrarySearchIndex(catalog, content);
        await index.RefreshAsync();
        await File.AppendAllTextAsync(catalog.GetVideoPath(video.RelativePath), "external change");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        content.BeforeRead = async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        using var cancellation = new CancellationTokenSource();
        var refresh = index.RefreshAsync(cancellationToken: cancellation.Token);
        await started.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.Single(await index.SearchAsync("original"));
        content.BeforeRead = null; content.Content = VideoMetadata.Empty with { Description = "updated searchable" };
        await index.RefreshAsync();
        Assert.Empty(await index.SearchAsync("original"));
        Assert.Single(await index.SearchAsync("updated"));
    }

    [Fact]
    public async Task RefreshDetectsRealExternalMkvEditsAndSaveUpdatesIndexImmediately()
    {
        var catalog = await CreateCatalogAsync();
        var tools = MediaTools.Discover();
        var source = Path.Combine(_root, "actual.mkv");
        var generated = await new MediaProcess().RunAsync(tools.Ffmpeg,
            ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", source], TimeSpan.FromSeconds(30));
        Assert.Equal(0, generated.ExitCode);
        var video = await new VideoImporter(catalog, tools).ImportAsync(source);
        var metadata = new MatroskaMetadataService(catalog, tools);
        await metadata.SaveAsync(video, VideoMetadata.Empty with { Description = "before external edit" });
        var index = new LibrarySearchIndex(catalog, metadata);
        await index.RefreshAsync();
        Assert.Single(await index.SearchAsync("before"));
        await metadata.SaveAsync(video, VideoMetadata.Empty with { Description = "outside application" });
        await index.RefreshAsync();
        Assert.Empty(await index.SearchAsync("before")); Assert.Single(await index.SearchAsync("outside"));
        var indexed = new IndexedMetadataService(metadata, index);
        await indexed.SaveAsync(video, VideoMetadata.Empty with
        {
            Description = "inside application", Chapters = [new VideoChapter(10, TimeSpan.Zero, "New chapter", "A searchable moment", ["newtag"])]
        });
        Assert.Empty(await index.SearchAsync("outside")); Assert.Single(await index.SearchAsync("inside")); Assert.Single(await index.SearchAsync("newtag"));
        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "DELETE FROM search_cache"; await command.ExecuteNonQueryAsync();
        }
        Assert.Empty(await index.SearchAsync("inside"));
        await index.RefreshAsync();
        Assert.Single(await index.SearchAsync("inside")); // Cache can be reconstructed from MKV alone.
    }

    [Fact]
    public async Task MigratesVersionOneCatalogWithoutLosingItsVideos()
    {
        var paths = new LibraryPaths(Path.Combine(_root, "legacy")); Directory.CreateDirectory(paths.RootDirectory);
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE videos(id TEXT PRIMARY KEY,relative_path TEXT UNIQUE NOT NULL,display_name TEXT NOT NULL,duration_ticks INTEGER NOT NULL,imported_at TEXT NOT NULL,recorded_at TEXT NOT NULL); PRAGMA user_version=1;";
            await command.ExecuteNonQueryAsync();
        }
        var catalog = new LibraryCatalog(paths);
        var video = new LibraryVideo(Guid.NewGuid(), "Videos/legacy.mkv", "Legacy", TimeSpan.FromSeconds(1), DateTimeOffset.Now, DateTimeOffset.Now);
        await catalog.AddAsync(video);
        await catalog.InitializeAsync(); await catalog.InitializeAsync();
        Assert.Equal(video.Id, Assert.Single(await catalog.ListAsync()).Id);
        Assert.NotNull(await new AlbumRepository(catalog).CreateAsync("Migrated album"));
    }

    private async Task<LibraryCatalog> CreateCatalogAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library"))); await catalog.InitializeAsync(); return catalog;
    }
    private static async Task<LibraryVideo> AddVideoAsync(LibraryCatalog catalog, string name)
    {
        var video = new LibraryVideo(Guid.NewGuid(), $"Videos/{Guid.NewGuid():N}.mkv", name, TimeSpan.FromSeconds(10), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(catalog.GetVideoPath(video.RelativePath), "source " + name); await catalog.AddAsync(video); return video;
    }
    private sealed class FakeMetadata : IVideoMetadataService
    {
        public VideoMetadata Content { get; set; } = VideoMetadata.Empty;
        public Func<CancellationToken, Task>? BeforeRead { get; set; }
        public async Task<VideoMetadata> ReadAsync(LibraryVideo video, CancellationToken cancellationToken = default)
        {
            if (BeforeRead is { } before) await before(cancellationToken); return Content;
        }
        public Task SaveAsync(LibraryVideo video, VideoMetadata metadata, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VideoThumbnail> CaptureThumbnailAsync(LibraryVideo video, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
