using System.Security.Cryptography;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class ImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-import-tests-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = MediaTools.Discover();

    [Fact]
    public async Task ImportsRealMp4WithoutChangingOriginalAndReopensCatalog()
    {
        var source = await CreateSourceAsync();
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var catalog = await CreateCatalogAsync();
        var importer = new VideoImporter(catalog, _tools);

        var video = await importer.ImportAsync(source);
        var second = await importer.ImportAsync(source);
        var importedMkv = catalog.GetVideoPath(video.RelativePath);
        var third = await importer.ImportAsync(importedMkv);

        Assert.NotEqual(video.RelativePath, second.RelativePath);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.True(File.Exists(catalog.GetVideoPath(video.RelativePath)));
        Assert.InRange(video.Duration.TotalSeconds, 1.8, 2.2);
        var reopened = new LibraryCatalog(catalog.Paths);
        await reopened.InitializeAsync();
        Assert.Equal(3, (await reopened.ListAsync()).Count);
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(importedMkv)),
            SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(third.RelativePath))));
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory, ".*"));
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-show_entries", "format=format_name:format_tags=title", "-of", "json", catalog.GetVideoPath(video.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.Contains("matroska", probe.StandardOutput);
        Assert.Contains("Test family video", probe.StandardOutput);
    }

    [Fact]
    public async Task RejectsTruncatedMkvEvenWhenItsHeadersRetainTheOriginalDuration()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "truncated.mkv");
        var generated = await new MediaProcess().RunAsync(_tools.Ffmpeg,
            ["-hide_banner", "-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=128x96:rate=10", "-t", "12", "-c:v", "libx264", "-pix_fmt", "yuv420p", source],
            TimeSpan.FromSeconds(30));
        Assert.Equal(0, generated.ExitCode);
        await using (var truncated = new FileStream(source, FileMode.Open, FileAccess.Write))
            truncated.SetLength(truncated.Length * 3 / 5);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", source], TimeSpan.FromSeconds(30));
        Assert.Equal(0, probe.ExitCode);
        Assert.Equal(12, double.Parse(probe.StandardOutput.Trim(), System.Globalization.CultureInfo.InvariantCulture));
        var catalog = await CreateCatalogAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => new VideoImporter(catalog, _tools).ImportAsync(source));

        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.Empty(await catalog.ListAsync());
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory));
    }

    [Fact]
    public async Task CancellationDuringPlaybackValidationCleansUpStagedMedia()
    {
        var source = await CreateSourceAsync();
        var catalog = await CreateCatalogAsync();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(update =>
        {
            if (update.Stage == "Checking complete video and audio playback") cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoImporter(catalog, _tools).ImportAsync(source, progress, cancellation.Token));

        Assert.True(File.Exists(source));
        Assert.Empty(await catalog.ListAsync());
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory));
    }

    [Fact]
    public async Task RejectsNonVideoWithoutPublishingFiles()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "not-a-video.mp4");
        await File.WriteAllTextAsync(source, "This is not media.");
        var catalog = await CreateCatalogAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => new VideoImporter(catalog, _tools).ImportAsync(source));

        Assert.Empty(await catalog.ListAsync());
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory));
        Assert.Equal("This is not media.", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task CancellationPreservesSourceAndPublishesNothing()
    {
        var source = await CreateSourceAsync();
        var catalog = await CreateCatalogAsync();
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(update =>
        {
            if (update.Stage == "Copying streams to MKV") cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VideoImporter(catalog, _tools).ImportAsync(source, progress, cancellation.Token));

        Assert.True(File.Exists(source));
        Assert.Empty(await catalog.ListAsync());
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory));
    }

    [Fact]
    public async Task FailedRegistrationRollsBackPublishedFile()
    {
        var source = await CreateSourceAsync();
        var catalog = await CreateCatalogAsync();
        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_import BEFORE INSERT ON videos BEGIN SELECT RAISE(ABORT, 'test registration failure'); END";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => new VideoImporter(catalog, _tools).ImportAsync(source));

        Assert.True(File.Exists(source));
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory));
        Assert.Empty(await catalog.ListAsync());
    }

    [Fact]
    public async Task FallsBackToTranscodeForMp4TextSubtitlesUnsupportedByMatroska()
    {
        var source = await CreateSourceAsync();
        var subtitles = Path.Combine(_root, "captions.srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:01,000\nFamily moment\n");
        var withSubtitles = Path.Combine(_root, "subtitled.mp4");
        var mux = await new MediaProcess().RunAsync(_tools.Ffmpeg,
            ["-v", "error", "-n", "-i", source, "-i", subtitles, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "mov_text", withSubtitles], TimeSpan.FromSeconds(30));
        Assert.Equal(0, mux.ExitCode);
        var catalog = await CreateCatalogAsync();

        var imported = await new VideoImporter(catalog, _tools).ImportAsync(withSubtitles);

        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "json", catalog.GetVideoPath(imported.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.Contains("subrip", probe.StandardOutput);
        Assert.True(File.Exists(withSubtitles));
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory, ".*"));
    }

    [Fact]
    public async Task CancellationStopsAnActiveMediaProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MediaProcess().RunAsync(_tools.Ffmpeg,
            ["-hide_banner", "-v", "error", "-re", "-f", "lavfi", "-i", "color=c=black:s=32x32:r=1", "-f", "null", "-"],
            TimeSpan.FromMinutes(1), cancellation.Token));
    }

    [Fact]
    public async Task OutputObserverFailureDoesNotStopDrainingProcessPipes()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MediaProcess().RunAsync(_tools.Ffmpeg,
            ["-hide_banner", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=32x32:r=10", "-t", "0.2", "-progress", "pipe:1", "-f", "null", "-"],
            TimeSpan.FromSeconds(10), outputReceived: _ => throw new InvalidOperationException("observer failed")));
        Assert.Equal("observer failed", error.Message);
    }

    [Fact]
    public async Task ProcessTimeoutStopsAnUnboundedMediaProcess()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => new MediaProcess().RunAsync(_tools.Ffmpeg,
            ["-hide_banner", "-v", "error", "-re", "-f", "lavfi", "-i", "color=c=black:s=32x32:r=1", "-f", "null", "-"],
            TimeSpan.FromMilliseconds(300)));
    }

    [Theory]
    [InlineData("../outside.mkv")]
    [InlineData("Videos/../../outside.mkv")]
    [InlineData("library.sqlite3")]
    public void CatalogRejectsPathsOutsideMediaDirectory(string path)
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        Assert.Throws<ArgumentException>(() => catalog.GetVideoPath(path));
    }

    [Fact]
    public async Task SettingsRoundTripSelectedRoot()
    {
        var settings = new LibrarySettings(Path.Combine(_root, "config", "settings.json"));
        Assert.Null(await settings.LoadRootAsync());
        await settings.SaveRootAsync(Path.Combine(_root, "library"));
        Assert.Equal(Path.Combine(_root, "library"), await settings.LoadRootAsync());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "config")));
    }

    private async Task<LibraryCatalog> CreateCatalogAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        await catalog.InitializeAsync();
        return catalog;
    }

    private async Task<string> CreateSourceAsync()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source with spaces.mp4");
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg,
            ["-hide_banner", "-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-metadata", "title=Test family video", path],
            TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class InlineProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => report(value);
    }
}
