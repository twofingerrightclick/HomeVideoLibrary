using System.Security.Cryptography;
using System.Text.Json;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class ClipEditingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-edits-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = MediaTools.Discover();

    [Fact]
    public async Task AccurateTrimAndSplitRemapChaptersAndChosenThumbnailsWithoutChangingOriginal()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateVideoAsync(catalog, "family", "160x120", 10, audio: true);
        var metadata = new MatroskaMetadataService(catalog, _tools);
        var thumbnail = await metadata.CaptureThumbnailAsync(source, TimeSpan.FromSeconds(1.5));
        await metadata.SaveAsync(source, new VideoMetadata("Family", "Description retained", ["family"], DateTimeOffset.Parse("1997-07-03T12:00:00Z"),
            [new VideoChapter(1, TimeSpan.Zero, "Arrival", "Start", ["arrival"]),
                new VideoChapter(2, TimeSpan.FromSeconds(1.5), "Cake", "Birthday moment", ["cake"])], [thumbnail]));
        var before = SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(source.RelativePath)));
        var editing = new ClipEditingService(catalog, _tools);

        var trimmed = Assert.Single(await editing.TrimAsync(source, TimeSpan.FromSeconds(0.7), TimeSpan.FromSeconds(2.1), "Trim result"));
        var trimMetadata = await metadata.ReadAsync(trimmed);
        Assert.InRange(trimmed.Duration.TotalSeconds, 1.35, 1.5);
        var trimmedFormat = await editing.GetSourceFormatAsync(trimmed);
        Assert.Equal(160, trimmedFormat.Width); Assert.Equal(120, trimmedFormat.Height); Assert.Equal("10/1", trimmedFormat.FrameRate);
        Assert.Equal("Trim result", trimMetadata.Title);
        Assert.Equal("Description retained", trimMetadata.Description);
        Assert.Equal(["family"], trimMetadata.Tags);
        Assert.Equal("Arrival", trimMetadata.Chapters[0].Title);
        Assert.Equal(TimeSpan.Zero, trimMetadata.Chapters[0].Position);
        Assert.Equal(TimeSpan.FromSeconds(0.8), trimMetadata.Chapters[1].Position);
        Assert.Equal(TimeSpan.FromSeconds(0.8), Assert.Single(trimMetadata.Thumbnails).Position);
        Assert.Equal("Birthday moment", trimMetadata.Chapters[1].Description);
        var split = await editing.SplitAsync(source, TimeSpan.FromSeconds(1.2), "Split result");
        Assert.Equal(2, split.Count);
        Assert.InRange(split[0].Duration.TotalSeconds, 1.15, 1.3);
        var second = await metadata.ReadAsync(split[1]);
        Assert.Equal("Arrival", second.Chapters[0].Title);
        Assert.Equal(TimeSpan.Zero, second.Chapters[0].Position);
        Assert.Equal(TimeSpan.FromSeconds(0.3), second.Chapters[1].Position);
        Assert.Equal(TimeSpan.FromSeconds(0.3), Assert.Single(second.Thumbnails).Position);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(source.RelativePath))));
        Assert.Equal(4, (await catalog.ListAsync()).Count);
        Assert.Empty(Directory.GetDirectories(catalog.Paths.VideosDirectory, ".edit-*"));
    }

    [Fact]
    public async Task MergesOrderedDifferentDimensionsRatesAndSilentClipWithUniqueChapterUids()
    {
        var catalog = await CreateCatalogAsync();
        var first = await CreateVideoAsync(catalog, "wide with audio", "160x90", 15, audio: true);
        var second = await CreateVideoAsync(catalog, "square silent", "96x96", 12, audio: false);
        var metadata = new MatroskaMetadataService(catalog, _tools);
        await metadata.SaveAsync(first, VideoMetadata.Empty with { Description = "First film", Tags = ["one"], Chapters = [new VideoChapter(7, TimeSpan.Zero, "First", "", [])] });
        await metadata.SaveAsync(second, VideoMetadata.Empty with { Description = "Second film", Tags = ["two"], Chapters = [new VideoChapter(7, TimeSpan.Zero, "Second", "", [])] });
        var firstHash = SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(first.RelativePath)));
        var secondHash = SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(second.RelativePath)));

        var output = Assert.Single(await new ClipEditingService(catalog, _tools).MergeAsync([second, first], "Ordered merge"));

        Assert.InRange(output.Duration.TotalSeconds, 5.9, 6.2);
        var content = await metadata.ReadAsync(output);
        Assert.Equal("Second", content.Chapters[0].Title); Assert.Equal(TimeSpan.Zero, content.Chapters[0].Position);
        Assert.Equal("First", content.Chapters[1].Title); Assert.InRange(content.Chapters[1].Position.TotalSeconds, 2.95, 3.1);
        Assert.NotEqual(content.Chapters[0].Uid, content.Chapters[1].Uid);
        Assert.Equal(["two", "one"], content.Tags);
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,width,height,r_frame_rate,sample_rate,channels", "-of", "json", catalog.GetVideoPath(output.RelativePath)], TimeSpan.FromSeconds(30));
        using var json = JsonDocument.Parse(probe.StandardOutput);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        Assert.Equal(96, video.GetProperty("width").GetInt32()); Assert.Equal(96, video.GetProperty("height").GetInt32());
        Assert.Equal("12/1", video.GetProperty("r_frame_rate").GetString());
        Assert.Equal(2, streams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio").GetProperty("channels").GetInt32());
        Assert.Equal(firstHash, SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(first.RelativePath))));
        Assert.Equal(secondHash, SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(second.RelativePath))));
    }

    [Fact]
    public async Task InvalidBoundariesPublishNothing()
    {
        var catalog = await CreateCatalogAsync(); var source = await CreateVideoAsync(catalog, "bounds", "64x48", 10, false);
        var editing = new ClipEditingService(catalog, _tools);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.TrimAsync(source, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.TrimAsync(source, TimeSpan.Zero, source.Duration + TimeSpan.FromSeconds(1), ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.SplitAsync(source, TimeSpan.Zero, ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.SplitAsync(source, source.Duration, ""));
        await Assert.ThrowsAsync<ArgumentException>(() => editing.MergeAsync([source], ""));
        Assert.Single(await catalog.ListAsync()); Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
    }

    [Fact]
    public async Task CancellingSplitAfterFirstOutputIsPreparedLeavesNoPartialBatch()
    {
        var catalog = await CreateCatalogAsync(); var source = await CreateVideoAsync(catalog, "cancel", "64x48", 10, false);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(source.RelativePath)));
        using var cancellation = new CancellationTokenSource(); var prepared = 0;
        var progress = new InlineProgress(update => { if (update.Stage == "Embedding remapped descriptions, chapters and thumbnails" && ++prepared == 2) cancellation.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClipEditingService(catalog, _tools)
            .SplitAsync(source, TimeSpan.FromSeconds(1.2), "Cancel split", progress, cancellation.Token));

        Assert.Single(await catalog.ListAsync()); Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
        Assert.Empty(Directory.GetDirectories(catalog.Paths.VideosDirectory, ".edit-*"));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(source.RelativePath))));
    }

    [Fact]
    public async Task SplitRegistrationFailureRollsBackAllRowsAndPublishedFiles()
    {
        var catalog = await CreateCatalogAsync(); var source = await CreateVideoAsync(catalog, "rollback", "64x48", 10, false);
        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_second_part BEFORE INSERT ON videos WHEN new.display_name LIKE '%part 2' BEGIN SELECT RAISE(ABORT,'fail second output'); END";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => new ClipEditingService(catalog, _tools).SplitAsync(source, TimeSpan.FromSeconds(1.2), "Rollback"));
        Assert.Single(await catalog.ListAsync()); Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
        Assert.Empty(Directory.GetDirectories(catalog.Paths.VideosDirectory, ".edit-*"));
    }

    private async Task<LibraryCatalog> CreateCatalogAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library"))); await catalog.InitializeAsync(); return catalog;
    }
    private async Task<LibraryVideo> CreateVideoAsync(LibraryCatalog catalog, string name, string dimensions, int fps, bool audio)
    {
        var id = Guid.NewGuid(); var relative = $"Videos/{id:N}.mkv";
        var arguments = new List<string> { "-v", "error", "-n", "-f", "lavfi", "-i", $"testsrc=size={dimensions}:rate={fps}" };
        if (audio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100"]);
        arguments.AddRange(["-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        if (audio) arguments.AddRange(["-c:a", "aac"]);
        arguments.Add(catalog.GetVideoPath(relative));
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, arguments, TimeSpan.FromSeconds(30)); Assert.Equal(0, result.ExitCode);
        var video = new LibraryVideo(id, relative, name, TimeSpan.FromSeconds(3), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await catalog.AddAsync(video); return video;
    }
    private sealed class InlineProgress(Action<ClipEditProgress> report) : IProgress<ClipEditProgress> { public void Report(ClipEditProgress value) => report(value); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
