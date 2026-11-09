using System.Security.Cryptography;
using System.Text.Json;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class MetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = MediaTools.Discover();

    [Fact]
    public async Task RoundTripsEmbeddedMetadataAndJpegTimestampsAfterReopen()
    {
        var (catalog, video) = await CreateVideoAsync();
        var service = new MatroskaMetadataService(catalog, _tools);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));
        var imageHash = SHA256.HashData(await File.ReadAllBytesAsync(thumbnail.ImagePath));
        var metadata = new VideoMetadata("Family <film>", "A description & memories", ["family", "arbitrary; tag"],
            new DateTimeOffset(1997, 7, 3, 12, 0, 0, TimeSpan.Zero),
            [new VideoChapter(100, TimeSpan.Zero, "Arrival", "At the house", ["hello"]),
                new VideoChapter(200, TimeSpan.FromSeconds(1.25), "Cake", "Make a wish", ["birthday", "family"])], [thumbnail]);

        await service.SaveAsync(video, metadata);
        Directory.Delete(catalog.Paths.CacheDirectory, recursive: true);
        var reopened = new MatroskaMetadataService(new LibraryCatalog(catalog.Paths), _tools);
        var read = await reopened.ReadAsync(video);

        Assert.Equal(metadata.Title, read.Title);
        Assert.Equal(metadata.Description, read.Description);
        Assert.Equal(metadata.Tags, read.Tags);
        Assert.Equal(metadata.RecordedAt, read.RecordedAt);
        Assert.Equal(2, read.Chapters.Count);
        Assert.Equal(200UL, read.Chapters[1].Uid);
        Assert.Equal(TimeSpan.FromSeconds(1.25), read.Chapters[1].Position);
        Assert.Equal("Make a wish", read.Chapters[1].Description);
        Assert.Equal(metadata.Chapters[1].Tags, read.Chapters[1].Tags);
        Assert.Equal(thumbnail.Id, Assert.Single(read.Thumbnails).Id);
        Assert.Equal(thumbnail.Position, read.Thumbnails[0].Position);
        Assert.Equal(imageHash, SHA256.HashData(await File.ReadAllBytesAsync(read.Thumbnails[0].ImagePath)));
        await reopened.SaveAsync(video, read with { Description = "Updated" });
        Assert.Equal("Updated", (await reopened.ReadAsync(video)).Description);
    }

    [Fact]
    public async Task ReadsExternalChaptersAndPreservesUnknownTagsEditionsAndAttachments()
    {
        var (catalog, video) = await CreateVideoAsync();
        var tags = Path.Combine(_root, "external-tags.xml");
        await File.WriteAllTextAsync(tags, """
            <Tags>
              <Tag><Targets/><Simple><Name>DESCRIPTION</Name><String>External description</String></Simple><Simple><Name>CUSTOM_NOTE</Name><String>Keep me</String></Simple></Tag>
              <Tag><Targets><TrackUID>987654</TrackUID></Targets><Simple><Name>TRACK_COMMENT</Name><String>Track survives</String></Simple></Tag>
            </Tags>
            """);
        var chapters = Path.Combine(_root, "external-chapters.xml");
        await File.WriteAllTextAsync(chapters, """
            <?xml version="1.0" encoding="UTF-8"?>
            <Chapters>
              <EditionEntry><EditionUID>111</EditionUID><EditionFlagDefault>1</EditionFlagDefault><ChapterAtom><ChapterUID>222</ChapterUID><ChapterTimeStart>00:00:00.000000000</ChapterTimeStart><ChapterDisplay><ChapterString>External chapter</ChapterString><ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom></EditionEntry>
              <EditionEntry><EditionUID>333</EditionUID><ChapterAtom><ChapterUID>444</ChapterUID><ChapterTimeStart>00:00:01.000000000</ChapterTimeStart><ChapterDisplay><ChapterString>Other edition</ChapterString><ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom></EditionEntry>
            </Chapters>
            """);
        var attachment = Path.Combine(_root, "external.txt");
        await File.WriteAllTextAsync(attachment, "This attachment belongs to someone else.");
        await ToolAsync(_tools.MkvPropEdit, [catalog.GetVideoPath(video.RelativePath), "--tags", "all:" + tags, "--chapters", chapters,
            "--attachment-name", "external.txt", "--attachment-mime-type", "text/plain", "--add-attachment", attachment]);
        var service = new MatroskaMetadataService(catalog, _tools);

        var original = await service.ReadAsync(video);
        Assert.Equal("External description", original.Description);
        Assert.Equal("External chapter", Assert.Single(original.Chapters).Title);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));
        await service.SaveAsync(video, original with { Description = "Edited", Thumbnails = [thumbnail] });
        var read = await service.ReadAsync(video);
        await service.SaveAsync(video, read with { Thumbnails = [] });

        var extractedTags = Path.Combine(_root, "result-tags.xml");
        var extractedChapters = Path.Combine(_root, "result-chapters.xml");
        await ToolAsync(_tools.MkvExtract, [catalog.GetVideoPath(video.RelativePath), "tags", extractedTags, "chapters", extractedChapters]);
        Assert.Contains("Keep me", await File.ReadAllTextAsync(extractedTags));
        Assert.Contains("Track survives", await File.ReadAllTextAsync(extractedTags));
        Assert.Contains("Other edition", await File.ReadAllTextAsync(extractedChapters));
        var identification = await ToolAsync(_tools.MkvMerge, ["-J", catalog.GetVideoPath(video.RelativePath)]);
        Assert.Contains("external.txt", identification.StandardOutput);
        Assert.DoesNotContain("hvl-thumbnail", identification.StandardOutput);
        Assert.DoesNotContain("home-video-library.thumbnails", identification.StandardOutput);
        using var identified = JsonDocument.Parse(identification.StandardOutput);
        var externalId = identified.RootElement.GetProperty("attachments").EnumerateArray()
            .Single(item => item.GetProperty("file_name").GetString() == "external.txt").GetProperty("id").GetInt32();
        var extractedAttachment = Path.Combine(_root, "external-extracted.txt");
        await ToolAsync(_tools.MkvExtract, [catalog.GetVideoPath(video.RelativePath), "attachments", $"{externalId}:{extractedAttachment}"]);
        Assert.Equal(await File.ReadAllTextAsync(attachment), await File.ReadAllTextAsync(extractedAttachment));
    }

    [Fact]
    public async Task RejectsInvalidPositionsAndMoreThanTwentyThumbnailsBeforeWriting()
    {
        var (catalog, video) = await CreateVideoAsync();
        var service = new MatroskaMetadataService(catalog, _tools);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(video.RelativePath)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(video, VideoMetadata.Empty with
        {
            Chapters = [new VideoChapter(1, video.Duration, "Invalid", "", [])]
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(video, VideoMetadata.Empty with
        {
            Thumbnails = Enumerable.Range(0, 21).Select(_ => new VideoThumbnail(Guid.NewGuid(), TimeSpan.Zero, "unused.jpg")).ToArray()
        }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(-1)));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(video.RelativePath))));
    }

    [Fact]
    public async Task CancellationAfterStagingStartsLeavesOriginalByteIdentical()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(catalog.Paths.VideosDirectory, "*.metadata.mkv");
        watcher.Created += (_, _) => cancellation.Cancel();
        watcher.EnableRaisingEvents = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MatroskaMetadataService(catalog, _tools)
            .SaveAsync(video, VideoMetadata.Empty with { Description = "Must not publish" }, cancellation.Token));

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory, ".*"));
        Assert.Empty(Directory.GetDirectories(catalog.Paths.CacheDirectory, "metadata-*"));
    }

    [Fact]
    public async Task CancelledOrFailedSaveLeavesOriginalByteIdenticalAndUnlocksNextWrite()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));
        var service = new MatroskaMetadataService(catalog, _tools);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(video, VideoMetadata.Empty, cancellation.Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.SaveAsync(video, VideoMetadata.Empty with
        {
            Thumbnails = [new VideoThumbnail(Guid.NewGuid(), TimeSpan.Zero, Path.Combine(_root, "missing.jpg"))]
        }));
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory, ".*"));
        Assert.Empty(Directory.GetDirectories(catalog.Paths.CacheDirectory, "metadata-*"));
        await service.SaveAsync(video, VideoMetadata.Empty with { Description = "Still writable" });
        Assert.Equal("Still writable", (await service.ReadAsync(video)).Description);
    }

    private async Task<(LibraryCatalog Catalog, LibraryVideo Video)> CreateVideoAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        await catalog.InitializeAsync();
        var source = Path.Combine(_root, "source.mkv");
        await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", source]);
        return (catalog, await new VideoImporter(catalog, _tools).ImportAsync(source));
    }

    private async Task<MediaProcessResult> ToolAsync(string tool, string[] arguments)
    {
        var result = await new MediaProcess().RunAsync(tool, arguments, TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
