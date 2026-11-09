using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
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
    public async Task PreservesCollectionSeasonAndExplicitAudioMetadataWhileEditingVideoLevelTags()
    {
        var (catalog, video) = await CreateVideoAsync();
        var fixture = new XDocument(new XElement("Tags",
            new[] { ("70", "COLLECTION"), ("60", "SEASON"), ("50", "ALBUM"), ("50", "MOVIE") }.Select(pair =>
                new XElement("Tag", new XElement("Targets", new XElement("TargetTypeValue", pair.Item1), new XElement("TargetType", pair.Item2)),
                    new XElement("Simple", new XElement("Name", "TITLE"), new XElement("String", pair.Item2 + " title")),
                    new XElement("Simple", new XElement("Name", "DESCRIPTION"), new XElement("String", pair.Item2 + " description")),
                    new XElement("Simple", new XElement("Name", "KEYWORDS"), new XElement("String", pair.Item2 + " keyword")),
                    new XElement("Simple", new XElement("Name", "DATE_RECORDED"), new XElement("String", "1997-07-03T12:00:00Z"))))));
        var tagsPath = Path.Combine(_root, "target-levels.xml");
        fixture.Save(tagsPath);
        var path = catalog.GetVideoPath(video.RelativePath);
        await ToolAsync(_tools.MkvPropEdit, [path, "--tags", "all:" + tagsPath]);
        var before = await ExtractTagsAsync(path, "before-levels.xml");
        var service = new MatroskaMetadataService(catalog, _tools);
        var read = await service.ReadAsync(video);
        Assert.Equal("MOVIE title", read.Title);
        Assert.Equal("MOVIE description", read.Description);
        Assert.Equal(["MOVIE keyword"], read.Tags);

        await service.SaveAsync(video, read with { Title = "Video title", Description = "Video edit", Tags = ["video"], RecordedAt = null });

        var after = await ExtractTagsAsync(path, "after-levels.xml");
        foreach (var type in new[] { "COLLECTION", "SEASON", "ALBUM" })
        {
            var originalTag = before.Root!.Elements("Tag").Single(tag => tag.Element("Targets")?.Element("TargetType")?.Value == type);
            var writtenTag = after.Root!.Elements("Tag").Single(tag => tag.Element("Targets")?.Element("TargetType")?.Value == type);
            Assert.True(XNode.DeepEquals(originalTag, writtenTag), type + " metadata changed");
        }
        Assert.Equal("Video edit", (await service.ReadAsync(video)).Description);
    }

    [Fact]
    public async Task PreservesMultiTargetChapterTrackTagsWhenChapterIsEditedAndRemoved()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var chaptersPath = Path.Combine(_root, "mixed-target-chapters.xml");
        await File.WriteAllTextAsync(chaptersPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <Chapters><EditionEntry><ChapterAtom><ChapterUID>222</ChapterUID><ChapterTimeStart>00:00:00.000000000</ChapterTimeStart><ChapterDisplay><ChapterString>Chapter</ChapterString><ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom></EditionEntry></Chapters>
            """);
        var tagsPath = Path.Combine(_root, "mixed-target-tags.xml");
        await File.WriteAllTextAsync(tagsPath, """
            <Tags>
              <Tag><Targets><ChapterUID>222</ChapterUID><TrackUID>987654</TrackUID></Targets><Simple><Name>DESCRIPTION</Name><String>Shared foreign description</String></Simple><Simple><Name>KEYWORDS</Name><String>Shared foreign keyword</String></Simple></Tag>
              <Tag><Targets><ChapterUID>222</ChapterUID></Targets><Simple><Name>DESCRIPTION</Name><String>Chapter description</String></Simple></Tag>
            </Tags>
            """);
        await ToolAsync(_tools.MkvPropEdit, [path, "--tags", "all:" + tagsPath, "--chapters", chaptersPath]);
        var before = await ExtractTagsAsync(path, "before-mixed.xml");
        var originalMixed = before.Root!.Elements("Tag").Single(tag => tag.Element("Targets")?.Element("TrackUID") is not null);
        var service = new MatroskaMetadataService(catalog, _tools);
        var read = await service.ReadAsync(video);
        Assert.Equal("Chapter description", Assert.Single(read.Chapters).Description);
        Assert.Empty(read.Chapters[0].Tags);

        await service.SaveAsync(video, read with { Chapters = [read.Chapters[0] with { Description = "Chapter edited", Tags = ["chapter"] }] });
        var edited = await ExtractTagsAsync(path, "edited-mixed.xml");
        Assert.True(XNode.DeepEquals(originalMixed, edited.Root!.Elements("Tag").Single(tag => tag.Element("Targets")?.Element("TrackUID") is not null)));
        await service.SaveAsync(video, (await service.ReadAsync(video)) with { Chapters = [] });

        var removed = await ExtractTagsAsync(path, "removed-mixed.xml");
        Assert.True(XNode.DeepEquals(originalMixed, removed.Root!.Elements("Tag").Single(tag => tag.Element("Targets")?.Element("TrackUID") is not null)));
        Assert.Empty((await service.ReadAsync(video)).Chapters);
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
        Assert.Empty(Directory.GetFileSystemEntries(catalog.Paths.ScratchDirectory));
        await service.SaveAsync(video, VideoMetadata.Empty with { Description = "Still writable" });
        Assert.Equal("Still writable", (await service.ReadAsync(video)).Description);
    }

    [Fact]
    public void ChapterTimesRoundTripExactlyThroughTheXmlFormat()
    {
        var random = new Random(1234);
        var samples = Enumerable.Range(0, 10_000).Select(_ => TimeSpan.FromTicks(random.NextInt64(0, TimeSpan.TicksPerDay * 3)))
            .Concat(Enumerable.Range(0, 10_000).Select(_ => TimeSpan.FromMilliseconds(random.Next(0, 4 * 3600 * 1000))));
        foreach (var sample in samples)
        {
            Assert.True(MatroskaMetadataXml.TryParseTime(MatroskaMetadataXml.FormatTime(sample), out var parsed));
            Assert.Equal(sample, parsed);
        }
        Assert.True(MatroskaMetadataXml.TryParseTime("00:00:00.285000001", out var nanoseconds));
        Assert.Equal(TimeSpan.FromTicks(2_850_000), nanoseconds);
        Assert.False(MatroskaMetadataXml.TryParseTime("00:61:00.000", out _));
    }

    [Fact]
    public async Task SavesMillisecondChapterAndNormalizesLineBreaksAndControlCharacters()
    {
        var (catalog, video) = await CreateVideoAsync();
        var service = new MatroskaMetadataService(catalog, _tools);

        await service.SaveAsync(video, VideoMetadata.Empty with
        {
            Title = "  Beach\r\nday ",
            Description = "line1\r\nline2\rline3\u000B",
            Chapters = [new VideoChapter(7, TimeSpan.FromSeconds(0.285), "Wave", "", [])],
        });

        var read = await service.ReadAsync(video);
        Assert.Equal("Beach day", read.Title);
        Assert.Equal("line1\nline2\nline3", read.Description);
        Assert.Equal(TimeSpan.FromSeconds(0.285), Assert.Single(read.Chapters).Position);
    }

    [Fact]
    public async Task SaveIsInPlaceAndLeavesPacketsAndDurationUnchanged()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var packetsBefore = await StreamHashAsync(path);
        var service = new MatroskaMetadataService(catalog, _tools);
        var durationBefore = (await service.ReadTextAsync(path)).Duration;
        // A copy-and-replace save would leave this handle on the old, unlinked file.
        await using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));

        await service.SaveAsync(video, VideoMetadata.Empty with { Title = "In place", Tags = ["one", "two"], Thumbnails = [thumbnail] });
        await service.SaveAsync(video, (await service.ReadAsync(video)) with { Description = "Second edit" });

        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(path)), await SHA256.HashDataAsync(original));
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(catalog.Paths.VideosDirectory).Select(Path.GetFileName));
        Assert.Equal(packetsBefore, await StreamHashAsync(path));
        Assert.Equal(durationBefore, (await service.ReadTextAsync(path)).Duration);
        Assert.Equal("Second edit", (await service.ReadAsync(video)).Description);
    }

    [Fact]
    public async Task FailedVerificationRollsBackTagsChaptersTitleAndThumbnails()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var service = new MatroskaMetadataService(catalog, _tools);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));
        await service.SaveAsync(video, VideoMetadata.Empty with
        {
            Title = "Original", Description = "Kept", Tags = ["kept"],
            Chapters = [new VideoChapter(5, TimeSpan.FromSeconds(1), "Original chapter", "", [])], Thumbnails = [thumbnail],
        });
        var before = await service.ReadAsync(video);
        service.AfterWriteForTesting = _ => throw new IOException("Simulated verification failure");

        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(video, VideoMetadata.Empty with { Title = "Replaced" }));

        service.AfterWriteForTesting = null;
        Directory.Delete(catalog.Paths.ThumbnailCacheDirectory, recursive: true);
        var after = await service.ReadAsync(video);
        Assert.Equal(("Original", "Kept"), (after.Title, after.Description));
        Assert.Equal(before.Tags, after.Tags);
        Assert.Equal("Original chapter", Assert.Single(after.Chapters).Title);
        Assert.Equal(thumbnail.Id, Assert.Single(after.Thumbnails).Id);
        Assert.Equal(SHA256.HashData(await File.ReadAllBytesAsync(thumbnail.ImagePath)), SHA256.HashData(await File.ReadAllBytesAsync(after.Thumbnails[0].ImagePath)));
        Assert.Empty(Directory.GetFileSystemEntries(catalog.Paths.ScratchDirectory));
    }

    [Fact]
    public async Task BrokenManifestStillReadsTextReportsWarningAndAllowsSaving()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var service = new MatroskaMetadataService(catalog, _tools);
        await service.SaveAsync(video, VideoMetadata.Empty with { Description = "Readable text" });
        var manifest = Path.Combine(_root, "home-video-library.thumbnails.v1.json");
        await File.WriteAllTextAsync(manifest, "{ not json");
        await ToolAsync(_tools.MkvPropEdit, [path, "--attachment-name", "home-video-library.thumbnails.v1.json",
            "--attachment-mime-type", "application/json", "--add-attachment", manifest]);

        var text = await service.ReadTextAsync(path);
        Assert.Equal("Readable text", text.Metadata.Description);
        Assert.NotEmpty(text.Warnings);
        Assert.Equal("Readable text", (await service.ReadAsync(video)).Description);
        await service.SaveAsync(video, (await service.ReadAsync(video)) with { Description = "Saved anyway" });
        var repaired = await service.ReadTextAsync(path);
        Assert.Equal("Saved anyway", repaired.Metadata.Description);
        Assert.Empty(repaired.Warnings);
    }

    [Fact]
    public async Task ReadTextReturnsThumbnailRefsWithoutExtractingAndExtractionIsCached()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var service = new MatroskaMetadataService(catalog, _tools);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));
        var expectedHash = SHA256.HashData(await File.ReadAllBytesAsync(thumbnail.ImagePath));
        await service.SaveAsync(video, VideoMetadata.Empty with { Thumbnails = [thumbnail] });
        Directory.Delete(catalog.Paths.ThumbnailCacheDirectory, recursive: true);

        var text = await service.ReadTextAsync(path);
        var reference = Assert.Single(text.Thumbnails);
        Assert.Equal((thumbnail.Id, thumbnail.Position), (reference.Id, reference.Position));
        Assert.False(Directory.Exists(catalog.Paths.ThumbnailCacheDirectory));

        var extracted = await service.ExtractThumbnailAsync(path, video.Id, reference);
        Assert.Equal(Path.Combine(catalog.Paths.ThumbnailCacheDirectory, video.Id.ToString("N"), thumbnail.Id.ToString("N") + ".jpg"), extracted);
        Assert.Equal(expectedHash, SHA256.HashData(await File.ReadAllBytesAsync(extracted)));
        // A cache hit needs no process, so it succeeds even with the video gone.
        File.Move(path, path + ".moved");
        Assert.Equal(extracted, await service.ExtractThumbnailAsync(path, video.Id, reference));
        File.Move(path + ".moved", path);
        await File.WriteAllTextAsync(extracted, "tampered");
        Assert.Equal(expectedHash, SHA256.HashData(await File.ReadAllBytesAsync(await service.ExtractThumbnailAsync(path, video.Id, reference))));
    }

    [Fact]
    public async Task KeepsPartialRecordedDateAndSplitsCommaSeparatedKeywords()
    {
        var (catalog, video) = await CreateVideoAsync();
        var path = catalog.GetVideoPath(video.RelativePath);
        var tagsPath = Path.Combine(_root, "partial-date.xml");
        await File.WriteAllTextAsync(tagsPath, """
            <Tags><Tag><Targets/><Simple><Name>DATE_RECORDED</Name><String>1997</String></Simple><Simple><Name>KEYWORDS</Name><String>a, b</String></Simple></Tag></Tags>
            """);
        await ToolAsync(_tools.MkvPropEdit, [path, "--tags", "all:" + tagsPath]);
        var service = new MatroskaMetadataService(catalog, _tools);

        var read = await service.ReadAsync(video);
        Assert.Equal(["a", "b"], read.Tags);
        Assert.Equal("1997", read.RecordedAtText);
        Assert.Equal(new DateTimeOffset(1997, 1, 1, 0, 0, 0, TimeSpan.Zero), read.RecordedAt);
        await service.SaveAsync(video, read with { Description = "Unrelated" });

        var saved = await service.ReadTextAsync(path);
        Assert.Equal("1997", saved.Metadata.RecordedAtText);
        Assert.Equal(["a", "b"], saved.Metadata.Tags);
    }

    [Fact]
    public async Task WriteStagedAddsExtraTagsAndKeepsExistingThumbnails()
    {
        var (catalog, video) = await CreateVideoAsync();
        var service = new MatroskaMetadataService(catalog, _tools);
        var thumbnail = await service.CaptureThumbnailAsync(video, TimeSpan.FromSeconds(0.5));
        await service.SaveAsync(video, VideoMetadata.Empty with { Thumbnails = [thumbnail] });
        var staged = Path.Combine(_root, "staged.mkv");
        File.Copy(catalog.GetVideoPath(video.RelativePath), staged);

        await service.WriteStagedAsync(staged, VideoMetadata.Empty with
        {
            Title = "Imported", RecordedAt = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero),
        }, new MetadataWriteOptions
        {
            ExtraGlobalTags = new Dictionary<string, string> { ["HVL_SOURCE_FINGERPRINT"] = "abc123" },
            KeepExistingThumbnails = true,
        });

        var text = await service.ReadTextAsync(staged);
        Assert.Equal("Imported", text.Metadata.Title);
        Assert.Equal(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero), text.Metadata.RecordedAt);
        Assert.Equal(thumbnail.Id, Assert.Single(text.Thumbnails).Id);
        Assert.Contains("abc123", await File.ReadAllTextAsync(await ExtractTagsPathAsync(staged, "staged-tags.xml")));
    }

    [Fact]
    public async Task CapturesAnamorphicFramesWithSquarePixels()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "anamorphic.mkv");
        await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc=size=720x480:rate=10", "-t", "1",
            "-vf", "setsar=8/9", "-c:v", "libx264", "-pix_fmt", "yuv420p", source]);
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        var output = Path.Combine(_root, "frame.jpg");

        await new MatroskaMetadataService(catalog, _tools).CaptureFrameAsync(source, TimeSpan.FromSeconds(0.5), output);

        var probe = await ToolAsync(_tools.Ffprobe, ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", output]);
        Assert.Equal("640,480", probe.StandardOutput.Trim());
    }

    private async Task<(LibraryCatalog Catalog, LibraryVideo Video)> CreateVideoAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        await catalog.InitializeAsync();
        var source = Path.Combine(_root, "source.mkv");
        await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p", source]);
        return (catalog, (await new VideoImporter(catalog, _tools).ImportAsync(source)).Video);
    }

    private async Task<MediaProcessResult> ToolAsync(string tool, string[] arguments)
    {
        var result = await new MediaProcess().RunAsync(tool, arguments, TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
        return result;
    }

    private async Task<string> StreamHashAsync(string path) =>
        (await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-i", path, "-map", "0:V", "-map", "0:a?", "-c", "copy", "-f", "streamhash", "-hash", "md5", "-"])).StandardOutput;

    private async Task<string> ExtractTagsPathAsync(string videoPath, string filename)
    {
        var path = Path.Combine(_root, filename);
        await ToolAsync(_tools.MkvExtract, [videoPath, "tags", path]);
        return path;
    }

    private async Task<XDocument> ExtractTagsAsync(string videoPath, string filename) =>
        XDocument.Load(await ExtractTagsPathAsync(videoPath, filename));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
