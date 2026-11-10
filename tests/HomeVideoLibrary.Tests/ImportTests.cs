using System.Security.Cryptography;
using System.Text.Json;
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

        var first = await importer.ImportAsync(source);
        var video = first.Video;
        var importedMkv = catalog.GetVideoPath(video.RelativePath);
        var third = await importer.ImportAsync(importedMkv);

        Assert.Equal(ImportOutcome.Imported, first.Outcome);
        Assert.Equal(ImportOutcome.Imported, third.Outcome);
        Assert.NotEqual(video.RelativePath, third.Video.RelativePath);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.True(File.Exists(importedMkv));
        Assert.InRange(video.Duration.TotalSeconds, 1.8, 2.2);
        Assert.Equal($"Videos/{video.Id:N}.mkv", video.RelativePath);
        var reopened = new LibraryCatalog(catalog.Paths);
        await reopened.InitializeAsync();
        Assert.Equal(2, (await reopened.ListAsync()).Count);
        Assert.Empty(Directory.GetFiles(catalog.Paths.VideosDirectory, ".*"));
        AssertNoStaging(catalog);
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-show_entries", "format=format_name", "-of", "json", importedMkv], TimeSpan.FromSeconds(30));
        Assert.Contains("matroska", probe.StandardOutput);
        Assert.Equal("Test family video", (await new MatroskaMetadataService(catalog, _tools).ReadTextAsync(importedMkv)).Metadata.Title);
    }

    [Fact]
    public async Task ReimportingTheSameSourceReturnsTheExistingVideo()
    {
        var source = await CreateSourceAsync();
        var catalog = await CreateCatalogAsync();
        var importer = new VideoImporter(catalog, _tools);
        var first = await importer.ImportAsync(source);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-3));

        var renamed = Path.Combine(_root, "renamed copy.mp4");
        File.Copy(source, renamed);

        var second = await importer.ImportAsync(source);
        var third = await importer.ImportAsync(renamed);

        Assert.Equal(ImportOutcome.AlreadyInLibrary, second.Outcome);
        Assert.Equal(ImportOutcome.AlreadyInLibrary, third.Outcome);
        Assert.Equal(first.Video.Id, second.Video.Id);
        Assert.Single(await catalog.ListAsync());
        Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
    }

    [Fact]
    public async Task FingerprintTellsApartFilesThatDifferOnlyInTheMiddle()
    {
        Directory.CreateDirectory(_root);
        var bytes = new byte[8 << 20];
        new Random(2).NextBytes(bytes);
        var first = Path.Combine(_root, "capture.bin");
        await File.WriteAllBytesAsync(first, bytes);
        // A different recording of the same length with the same lead-in and tail differs throughout its middle.
        var middle = bytes.AsSpan(bytes.Length / 2 - (512 << 10), 1 << 20);
        var original = middle.ToArray();
        new Random(3).NextBytes(middle);
        var second = Path.Combine(_root, "other", "capture.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        await File.WriteAllBytesAsync(second, bytes);
        original.CopyTo(bytes.AsSpan(bytes.Length / 2 - (512 << 10)));
        var copy = Path.Combine(_root, "renamed.bin");
        await File.WriteAllBytesAsync(copy, bytes);

        var fingerprint = await VideoImporter.ComputeFingerprintAsync(first, CancellationToken.None);

        Assert.NotEqual(fingerprint, await VideoImporter.ComputeFingerprintAsync(second, CancellationToken.None));
        Assert.Equal(fingerprint, await VideoImporter.ComputeFingerprintAsync(copy, CancellationToken.None));
    }

    [Fact]
    public async Task MatroskaThatIsWebMOrStartsLateIsRewrittenLosslesslyOnImport()
    {
        Directory.CreateDirectory(_root);
        var webm = Path.Combine(_root, "clip.webm");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-c:v", "libvpx", "-b:v", "200k", webm]);
        var plain = Path.Combine(_root, "plain.mkv");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "4", "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", plain]);
        var chapters = Path.Combine(_root, "chapters.txt");
        await File.WriteAllTextAsync(chapters, "CHAPTER01=00:00:03.000\nCHAPTER01NAME=Late\n");
        var late = Path.Combine(_root, "late.mkv");
        Assert.Equal(0, (await new MediaProcess().RunAsync(_tools.MkvMerge, ["-q", "-o", late, "--sync", "0:1500", "--chapters", chapters, plain],
            TimeSpan.FromSeconds(30))).ExitCode);
        var catalog = await CreateCatalogAsync();
        var importer = new VideoImporter(catalog, _tools);

        var webmResult = await importer.ImportAsync(webm);
        var lateResult = await importer.ImportAsync(late);

        var webmBytes = await File.ReadAllBytesAsync(catalog.GetVideoPath(webmResult.Video.RelativePath));
        Assert.Contains("matroska", System.Text.Encoding.ASCII.GetString(webmBytes, 0, 64), StringComparison.Ordinal);
        Assert.Contains(webmResult.Warnings, warning => warning.Contains("WebM", StringComparison.Ordinal));
        var latePath = catalog.GetVideoPath(lateResult.Video.RelativePath);
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe, ["-v", "error", "-show_entries", "format=start_time:chapter=start_time",
            "-of", "json", latePath], TimeSpan.FromSeconds(30));
        using var json = JsonDocument.Parse(probe.StandardOutput);
        Assert.Equal("0.000000", json.RootElement.GetProperty("format").GetProperty("start_time").GetString());
        Assert.Equal("1.500000", json.RootElement.GetProperty("chapters")[0].GetProperty("start_time").GetString());
    }

    [Fact]
    public async Task RecoveryFinishesCommittedPublicationsEvenWithUnreadableRowsAndQuarantinesTheRest()
    {
        var catalog = await CreateCatalogAsync();
        var committed = new LibraryVideo(Guid.NewGuid(), "", "committed", TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        committed = committed with { RelativePath = LibraryPaths.VideoRelativePath(committed.Id) };
        await catalog.AddAsync(committed);
        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE videos SET imported_at = 'not a date' WHERE id = '{committed.Id}'";
            await command.ExecuteNonQueryAsync();
        }
        var orphan = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(catalog.Paths.VideosDirectory, $".{committed.Id:N}.pending.mkv"), "committed video");
        await File.WriteAllTextAsync(Path.Combine(catalog.Paths.VideosDirectory, $".{orphan:N}.pending.mkv"), "orphaned video");
        using var owner = LibraryLock.TryAcquire(catalog.Paths)!;

        var first = await LibraryPublisher.RecoverAsync(catalog, owner);
        var again = await LibraryPublisher.RecoverAsync(catalog, owner);

        Assert.Equal((1, 1, 0), (first.Finished, first.Quarantined, first.Stuck));
        Assert.Equal(PublishRecovery.None, again);
        Assert.Equal("committed video", await File.ReadAllTextAsync(catalog.GetVideoPath(committed.RelativePath)));
        Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
        Assert.Equal("orphaned video", await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(LibraryPublisher.OrphanedDirectory(catalog.Paths),
            $"{orphan:N}-*.mkv"))));
    }

    [Fact]
    public async Task RecoveryReportsStuckPendingFilesAndRegistrationLessVideosButTouchesNeither()
    {
        var catalog = await CreateCatalogAsync();
        var blocked = new LibraryVideo(Guid.NewGuid(), "", "blocked", TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        blocked = blocked with { RelativePath = LibraryPaths.VideoRelativePath(blocked.Id) };
        await catalog.AddAsync(blocked);
        await File.WriteAllTextAsync(catalog.GetFinalVideoPath(blocked.RelativePath), "in the way");
        await File.WriteAllTextAsync(catalog.Paths.PendingVideoPath(blocked.Id), "pending");
        using var owner = LibraryLock.TryAcquire(catalog.Paths)!;
        await LibraryPublisher.RecoverAsync(catalog, owner);
        var unregistered = Path.Combine(catalog.Paths.VideosDirectory, $"{Guid.NewGuid():N}.mkv");
        await File.WriteAllTextAsync(unregistered, "lost commit");

        var started = DateTime.UtcNow;
        var recovery = await LibraryPublisher.RecoverAsync(catalog, owner);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.Equal(1, recovery.Stuck);
        Assert.Equal([Path.GetFileName(unregistered)], recovery.Unregistered);
        Assert.Equal("pending", await File.ReadAllTextAsync(catalog.Paths.PendingVideoPath(blocked.Id)));
        Assert.Equal("lost commit", await File.ReadAllTextAsync(unregistered));
    }

    [Fact]
    public async Task ACommittedVideoWhoseRenameIsRefusedIsPublishedAndReadableUntilRecoveryFinishesIt()
    {
        var source = await CreateSourceAsync();
        var catalog = await CreateCatalogAsync();
        var staged = Path.Combine(catalog.Paths.ScratchDirectory, "staged.mkv");
        Directory.CreateDirectory(catalog.Paths.ScratchDirectory);
        File.Copy(source, staged);
        var id = Guid.NewGuid();
        var video = new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), "blocked", TimeSpan.FromSeconds(2), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var final = catalog.GetVideoPath(video.RelativePath);
        // A directory of the same name refuses the rename, as a share violation would.
        Directory.CreateDirectory(final);

        var result = await LibraryPublisher.PublishAsync(catalog, [(video, staged)], CancellationToken.None);

        Assert.Equal([id], result.Unfinished);
        Assert.True(await catalog.ContainsVideoAsync(id));
        Assert.Equal(catalog.Paths.PendingVideoPath(id), catalog.GetVideoPath(video.RelativePath));
        Directory.Delete(final);
        Assert.Equal(1, await LibraryPublisher.FinishCommittedAsync(catalog));
        Assert.Equal(final, catalog.GetVideoPath(video.RelativePath));
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task FinishingDuringOtherPublicationsLeavesUncommittedPendingFilesAlone()
    {
        var catalog = await CreateCatalogAsync();
        var inFlight = catalog.Paths.PendingVideoPath(Guid.NewGuid());
        await File.WriteAllTextAsync(inFlight, "being published");

        Assert.Equal(0, await LibraryPublisher.FinishCommittedAsync(catalog));

        Assert.True(File.Exists(inFlight));
    }

    [Fact]
    public async Task ASecondRowWithTheSameSourceFingerprintIsRefusedByTheCatalog()
    {
        var catalog = await CreateCatalogAsync();
        LibraryVideo Video() => new LibraryVideo(Guid.NewGuid(), "", "same source", TimeSpan.FromSeconds(1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            is var video ? video with { RelativePath = LibraryPaths.VideoRelativePath(video.Id), SourceFingerprint = "v2:abc" } : null!;
        await catalog.AddAsync(Video());

        var failure = await Assert.ThrowsAsync<SqliteException>(() => catalog.AddAsync(Video()));

        Assert.True(LibraryCatalog.IsDuplicateSourceFingerprint(failure));
    }

    [Fact]
    public async Task DamagedMatroskaIsImportedWithAWarningAndOneWithoutDurationIsRewritten()
    {
        Directory.CreateDirectory(_root);
        var damaged = Path.Combine(_root, "damaged.mkv");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "6", "-c:v", "mpeg2video", "-g", "12", damaged]);
        var bytes = await File.ReadAllBytesAsync(damaged);
        new Random(4).NextBytes(bytes.AsSpan(bytes.Length / 2, 4000));
        await File.WriteAllBytesAsync(damaged, bytes);
        var live = Path.Combine(_root, "live.mkv");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-live", "1", live]);
        var catalog = await CreateCatalogAsync();
        var importer = new VideoImporter(catalog, _tools);

        var damagedResult = await importer.ImportAsync(damaged);
        var liveResult = await importer.ImportAsync(live);

        Assert.Contains(damagedResult.Warnings, warning => warning.Contains("damaged", StringComparison.Ordinal));
        Assert.Contains(liveResult.Warnings, warning => warning.Contains("no duration", StringComparison.Ordinal));
        Assert.InRange(liveResult.Video.Duration.TotalSeconds, 2.9, 3.1);
    }

    [Fact]
    public async Task ASourceThatChangesDuringAConversionIsRefused()
    {
        var source = await CreateSourceAsync();
        var catalog = await CreateCatalogAsync();
        var progress = new InlineProgress(update =>
        {
            if (update.Stage == "Copying streams to MKV") File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        });

        var error = await Assert.ThrowsAsync<IOException>(() => new VideoImporter(catalog, _tools).ImportAsync(source, progress));

        Assert.Contains("changed while it was being imported", error.Message, StringComparison.Ordinal);
        Assert.Empty(await catalog.ListAsync());
        AssertNoStaging(catalog);
    }

    [Fact]
    public async Task VariableFrameRateFootageImportsWithoutDamageWarnings()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "phone.mp4");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=160x120:rate=30", "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "6",
            "-vf", "select='not(between(n,60,70))'", "-fps_mode", "vfr", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source]);
        var catalog = await CreateCatalogAsync();

        var result = await new VideoImporter(catalog, _tools).ImportAsync(source);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ImportedMkvCarriesTitleRecordedDateAndSourceFingerprint()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "Birthday 1997.mp4");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-metadata", "creation_time=1970-01-01T00:00:05Z", source]);
        var modified = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        var catalog = await CreateCatalogAsync();

        var result = await new VideoImporter(catalog, _tools).ImportAsync(source);

        var path = catalog.GetVideoPath(result.Video.RelativePath);
        var text = await new MatroskaMetadataService(catalog, _tools).ReadTextAsync(path);
        Assert.Equal("Birthday 1997", text.Metadata.Title);
        Assert.Equal(new DateTimeOffset(modified), text.Metadata.RecordedAt);
        Assert.NotEqual("", text.Metadata.RecordedAtText);
        Assert.Equal(new DateTimeOffset(modified), result.Video.RecordedAt);
        Assert.Equal(TimeSpan.Zero, result.Video.RecordedAt.Offset);
        Assert.NotNull(result.Video.SourceFingerprint);
        var tags = await ExtractTagsAsync(path);
        Assert.Contains($"<String>{result.Video.SourceFingerprint}</String>", tags);
        Assert.Contains("HVL_SOURCE_FINGERPRINT", tags);
        Assert.Contains("<String>Birthday 1997.mp4</String>", tags);
    }

    [Fact]
    public async Task Mp4TextSubtitlesAreConvertedWhileVideoIsCopied()
    {
        var source = await CreateSourceAsync();
        var subtitles = Path.Combine(_root, "captions.srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:01,000\nFamily moment\n");
        var withSubtitles = Path.Combine(_root, "subtitled.mp4");
        await RunFfmpegAsync(["-i", source, "-i", subtitles, "-map", "0", "-map", "1", "-c", "copy", "-c:s", "mov_text", withSubtitles]);
        var catalog = await CreateCatalogAsync();

        var imported = await new VideoImporter(catalog, _tools).ImportAsync(withSubtitles);

        var path = catalog.GetVideoPath(imported.Video.RelativePath);
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-select_streams", "s", "-show_entries", "stream=codec_name", "-of", "json", path], TimeSpan.FromSeconds(30));
        Assert.Contains("subrip", probe.StandardOutput);
        Assert.Equal(await VideoPacketHashesAsync(withSubtitles), await VideoPacketHashesAsync(path));
        Assert.Contains(imported.Warnings, warning => warning.Contains("mov_text", StringComparison.Ordinal));
        Assert.True(File.Exists(withSubtitles));
        AssertNoStaging(catalog);
    }

    [Fact]
    public async Task DamagedTransportStreamImportsWithAWarning()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "tape.ts");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "6",
            "-c:v", "mpeg2video", "-g", "12", "-c:a", "mp2", "-f", "mpegts", source]);
        CorruptTransportPackets(source);
        var catalog = await CreateCatalogAsync();

        var result = await new VideoImporter(catalog, _tools).ImportAsync(source);

        Assert.Equal(ImportOutcome.Imported, result.Outcome);
        Assert.Contains(result.Warnings, warning => warning.Contains("damaged", StringComparison.Ordinal));
        Assert.InRange(result.Video.Duration.TotalSeconds, 5, 6.5);
    }

    [Fact]
    public async Task CoverArtIsDroppedInsteadOfBecomingASecondVideoTrack()
    {
        var source = await CreateSourceAsync();
        var cover = Path.Combine(_root, "cover.jpg");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "color=c=red:s=32x32", "-frames:v", "1", cover]);
        var withCover = Path.Combine(_root, "with cover.mp4");
        await RunFfmpegAsync(["-i", source, "-i", cover, "-map", "0", "-map", "1", "-c", "copy", "-disposition:v:1", "attached_pic", withCover]);
        var catalog = await CreateCatalogAsync();

        var result = await new VideoImporter(catalog, _tools).ImportAsync(withCover);

        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe, ["-v", "error", "-select_streams", "v", "-show_entries", "stream=index",
            "-of", "csv=p=0", catalog.GetVideoPath(result.Video.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.Single(probe.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(result.Warnings, warning => warning.Contains("Cover art", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RawVideoStreamIsRejectedWithAFriendlyMessage()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "raw.h264");
        await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "1", "-c:v", "libx264", "-f", "h264", source]);
        var catalog = await CreateCatalogAsync();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new VideoImporter(catalog, _tools).ImportAsync(source));

        Assert.Contains("raw video stream", error.Message, StringComparison.Ordinal);
        Assert.Empty(await catalog.ListAsync());
    }

    [Theory]
    [InlineData("[matroska @ 0x1] Subtitle codec 94213 is not supported.\nCould not write header (incorrect codec parameters ?): Function not implemented", true)]
    [InlineData("Subtitle encoding currently only possible from text to text or bitmap to bitmap", true)]
    [InlineData("[matroska @ 0x1] Can't write packet with unknown timestamp", true)]
    [InlineData("Error writing trailer: No space left on device", false)]
    [InlineData("/Volumes/Tape/a.mp4: Input/output error", false)]
    public void ClassifiesCodecIncompatibilities(string stderr, bool expected) =>
        Assert.Equal(expected, VideoImporter.IsCodecIncompatibility(stderr));

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
        AssertNoStaging(catalog);
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
        AssertNoStaging(catalog);
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
        AssertNoStaging(catalog);
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
        AssertNoStaging(catalog);
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

    private static void AssertNoStaging(LibraryCatalog catalog) =>
        Assert.Empty(Directory.Exists(catalog.Paths.ScratchDirectory) ? Directory.GetFileSystemEntries(catalog.Paths.ScratchDirectory, "import-*") : []);

    private async Task RunFfmpegAsync(IEnumerable<string> arguments)
    {
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-hide_banner", "-v", "error", "-n", .. arguments], TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private async Task<string[]> VideoPacketHashesAsync(string path) =>
        (await new MediaProcess().RunAsync(_tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_data_hash", "MD5",
            "-show_entries", "packet=data_hash", "-of", "csv=p=0", path], TimeSpan.FromSeconds(30)))
        .StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private async Task<string> ExtractTagsAsync(string mkv)
    {
        var tags = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        await new MediaProcess().RunAsync(_tools.MkvExtract, [mkv, "tags", tags], TimeSpan.FromSeconds(30));
        return await File.ReadAllTextAsync(tags);
    }

    /// <summary>Overwrites the payload of a few transport packets, like dropouts on a digitized tape.</summary>
    internal static void CorruptTransportPackets(string path, IReadOnlyList<double>? fractions = null)
    {
        var bytes = File.ReadAllBytes(path);
        var packets = bytes.Length / 188;
        var random = new Random(1);
        foreach (var fraction in fractions ?? [0.3, 0.5, 0.7])
            for (var packet = (int)(packets * fraction); packet < (int)(packets * fraction) + 3; packet++)
                random.NextBytes(bytes.AsSpan(packet * 188 + 4, 184));
        File.WriteAllBytes(path, bytes);
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
