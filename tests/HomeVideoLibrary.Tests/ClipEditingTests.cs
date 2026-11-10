using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class ClipEditingTests : IDisposable
{
    private static readonly TimeSpan TwoFrames = TimeSpan.FromSeconds(0.2);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-edits-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = MediaTools.Discover();

    [Fact]
    public async Task FastTrimCopiesPacketsKeepsEveryTrackAndShiftsMetadataBySnappedStart()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateFixtureAsync(catalog, "family");
        var metadata = new MatroskaMetadataService(catalog, _tools);
        var thumbnail = await metadata.CaptureThumbnailAsync(source, TimeSpan.FromSeconds(3.5));
        await metadata.SaveAsync(source, new VideoMetadata("Family", "Description retained", ["family"], DateTimeOffset.Parse("1997-07-03T12:00:00Z"),
            [new VideoChapter(1, TimeSpan.Zero, "Arrival", "Start", ["arrival"]),
                new VideoChapter(2, TimeSpan.FromSeconds(3.5), "Cake", "Birthday moment", ["cake"])], [thumbnail]));
        var before = await HashAsync(catalog, source);
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanTrimAsync(source, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4.5), ClipCutMode.Fast);
        var trimmed = Assert.Single(await editing.TrimAsync(source, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4.5), "Trim result",
            mode: ClipCutMode.Fast));

        Assert.True(plan.IsLossless);
        Assert.Equal(TimeSpan.FromSeconds(1), plan.ActualStart);
        Assert.Equal(TimeSpan.FromSeconds(5), plan.ActualEnd);
        Assert.InRange((trimmed.Duration - (plan.ActualEnd - plan.ActualStart)).Duration(), TimeSpan.Zero, TwoFrames);
        Assert.Equal(await PacketHashesAsync(catalog.GetVideoPath(source.RelativePath), plan.ActualStart, plan.ActualEnd),
            await PacketHashesAsync(catalog.GetVideoPath(trimmed.RelativePath), TimeSpan.Zero, TimeSpan.MaxValue));
        Assert.Equal(await TracksAsync(catalog, source), await TracksAsync(catalog, trimmed));
        var content = await metadata.ReadAsync(trimmed);
        Assert.Equal("Trim result", content.Title);
        Assert.Equal("Description retained", content.Description);
        Assert.Equal(["family"], content.Tags);
        Assert.Equal(["Arrival", "Cake"], content.Chapters.Select(chapter => chapter.Title));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(2.5)], content.Chapters.Select(chapter => chapter.Position));
        Assert.Equal(content.Chapters.Count, content.Chapters.Select(chapter => chapter.Uid).Distinct().Count());
        Assert.Equal(TimeSpan.FromSeconds(2.5), Assert.Single(content.Thumbnails).Position);
        Assert.Equal(DateTimeOffset.Parse("1997-07-03T12:00:01Z"), content.RecordedAt);
        Assert.Equal(1, await CountGlobalTitlesAsync(catalog.GetVideoPath(trimmed.RelativePath)));
        Assert.Equal(before, await HashAsync(catalog, source));
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task PreciseTrimAtNonKeyframesReencodesOnlyNextToTheCuts()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateFixtureAsync(catalog, "precise");
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanTrimAsync(source, TimeSpan.FromSeconds(1.35), TimeSpan.FromSeconds(4.65));
        var trimmed = Assert.Single(await editing.TrimAsync(source, TimeSpan.FromSeconds(1.35), TimeSpan.FromSeconds(4.65), "Precise"));

        Assert.False(plan.IsLossless);
        Assert.Equal(TimeSpan.FromSeconds(1.3), plan.ReencodedDuration); // 1.4..2.0 and 4.0..4.7.
        Assert.Equal(TimeSpan.FromSeconds(1.4), plan.ActualStart);
        Assert.Equal(TimeSpan.FromSeconds(4.7), plan.ActualEnd);
        Assert.InRange((trimmed.Duration - TimeSpan.FromSeconds(3.3)).Duration(), TimeSpan.Zero, TwoFrames);
        var output = catalog.GetVideoPath(trimmed.RelativePath);
        // Frames 1.4..1.9 are re-encoded; 2.0..3.9 are copied and must decode identically to the source.
        var outputFrames = await DecodedFrameHashesAsync(output);
        var sourceFrames = await DecodedFrameHashesAsync(catalog.GetVideoPath(source.RelativePath));
        Assert.Equal(33, outputFrames.Count);
        Assert.Equal(sourceFrames.Skip(20).Take(20), outputFrames.Skip(6).Take(20));
        Assert.Equal("", await DecodeErrorsAsync(output));
        Assert.Equal(await TracksAsync(catalog, source), await TracksAsync(catalog, trimmed));
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task SplitPartitionsSourcePacketsLosslesslyAtTheNearestKeyframe()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateFixtureAsync(catalog, "split");
        var metadata = new MatroskaMetadataService(catalog, _tools);
        await metadata.SaveAsync(source, VideoMetadata.Empty with
        {
            Title = "Split me",
            Chapters = [new VideoChapter(1, TimeSpan.Zero, "Arrival", "", []), new VideoChapter(2, TimeSpan.FromSeconds(3.5), "Cake", "", [])],
        });
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanSplitAsync(source, TimeSpan.FromSeconds(3.2));
        var parts = await editing.SplitAsync(source, TimeSpan.FromSeconds(3.2), "Split result");

        Assert.Equal(TimeSpan.FromSeconds(3), plan.ActualStart);
        Assert.Equal(2, parts.Count);
        var sourcePath = catalog.GetVideoPath(source.RelativePath);
        var firstPart = await PacketHashesAsync(catalog.GetVideoPath(parts[0].RelativePath), TimeSpan.Zero, TimeSpan.MaxValue);
        var secondPart = await PacketHashesAsync(catalog.GetVideoPath(parts[1].RelativePath), TimeSpan.Zero, TimeSpan.MaxValue);
        Assert.Equal(await PacketHashesAsync(sourcePath, TimeSpan.Zero, TimeSpan.MaxValue), firstPart.Concat(secondPart).ToArray());
        var second = await metadata.ReadAsync(parts[1]);
        Assert.Equal(["Arrival", "Cake"], second.Chapters.Select(chapter => chapter.Title));
        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(0.5)], second.Chapters.Select(chapter => chapter.Position));
        Assert.Equal(3, (await catalog.ListAsync()).Count);
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task CompatibleMergeIsCopiedAndGetsAChapterAtEachJoin()
    {
        var catalog = await CreateCatalogAsync();
        var first = await CreateFixtureAsync(catalog, "first");
        var second = await CreateFixtureAsync(catalog, "second");
        var metadata = new MatroskaMetadataService(catalog, _tools);
        await metadata.SaveAsync(first, VideoMetadata.Empty with { Title = "Morning", Description = "First film", Tags = ["one"] });
        await metadata.SaveAsync(second, VideoMetadata.Empty with
        {
            Description = "Second film", Tags = ["two", "One"], Chapters = [new VideoChapter(9, TimeSpan.Zero, "Afternoon", "", [])],
        });
        var editing = new ClipEditingService(catalog, _tools);

        Assert.True((await editing.PlanMergeAsync([first, second])).IsLossless);
        var output = Assert.Single(await editing.MergeAsync([first, second], "Whole day"));

        var path = catalog.GetVideoPath(output.RelativePath);
        Assert.InRange((output.Duration - TimeSpan.FromSeconds(12)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(0.25));
        Assert.Equal(await CodecPrivateAsync(catalog.GetVideoPath(first.RelativePath)), await CodecPrivateAsync(path));
        Assert.Equal(await TracksAsync(catalog, first), await TracksAsync(catalog, output));
        var content = await metadata.ReadAsync(output);
        Assert.Equal(["Morning", "Afternoon"], content.Chapters.Select(chapter => chapter.Title));
        Assert.InRange(content.Chapters[1].Position.TotalSeconds, 6, 6.1);
        Assert.Equal("First film\nSecond film", content.Description);
        Assert.Equal(["one", "two"], content.Tags);
        Assert.Equal(1, await CountGlobalTitlesAsync(path));
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task IncompatibleMergeReencodesOnlyTheMinorityClip()
    {
        var catalog = await CreateCatalogAsync();
        var square = await CreateSimpleAsync(catalog, "square silent", "96x96", 12, 4, audio: false);
        var wide = await CreateSimpleAsync(catalog, "wide with audio", "160x90", 15, 3, audio: true);
        var squareHash = await HashAsync(catalog, square);
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanMergeAsync([wide, square]);
        var output = Assert.Single(await editing.MergeAsync([wide, square], "Ordered merge"));

        Assert.Equal(1, plan.ReencodedClips);
        Assert.True(plan.Clips[0].ReencodesVideo);
        Assert.Equal("it is 160×90 and the merged video is 96×96", plan.Clips[0].Reason);
        Assert.Equal((false, null), (plan.Clips[1].ReencodesVideo, plan.Clips[1].Reason));
        var path = catalog.GetVideoPath(output.RelativePath);
        var streams = await StreamsAsync(path);
        var video = streams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        Assert.Equal(96, video.GetProperty("width").GetInt32());
        Assert.Equal(96, video.GetProperty("height").GetInt32());
        Assert.Equal("12/1", video.GetProperty("r_frame_rate").GetString());
        Assert.Single(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
        Assert.InRange((output.Duration - TimeSpan.FromSeconds(7)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(0.25));
        // The dominant (longer) clip is copied: its frames decode identically after the 3 s re-encoded clip.
        var outputFrames = await DecodedFrameHashesAsync(path);
        var squareFrames = await DecodedFrameHashesAsync(catalog.GetVideoPath(square.RelativePath));
        Assert.Equal(36, outputFrames.Count - squareFrames.Count);
        Assert.Equal(squareFrames, outputFrames.Skip(36).ToArray());
        var content = await new MatroskaMetadataService(catalog, _tools).ReadAsync(output);
        Assert.Equal(["wide with audio", "square silent"], content.Chapters.Select(chapter => chapter.Title));
        Assert.Equal(squareHash, await HashAsync(catalog, square));
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task MergingOnlySilentClipsCreatesAVideoWithoutAudio()
    {
        var catalog = await CreateCatalogAsync();
        var first = await CreateSimpleAsync(catalog, "silent one", "96x96", 10, 2, audio: false);
        var second = await CreateSimpleAsync(catalog, "silent two", "64x48", 10, 2, audio: false);

        var output = Assert.Single(await new ClipEditingService(catalog, _tools).MergeAsync([first, second], "Silent"));

        var streams = await StreamsAsync(catalog.GetVideoPath(output.RelativePath));
        Assert.DoesNotContain(streams, stream => stream.GetProperty("codec_type").GetString() == "audio");
        Assert.InRange(output.Duration.TotalSeconds, 3.8, 4.2);
    }

    [Fact]
    public async Task WholeVideoEndTakenFromALongerAudioTrackMeansTheVideosEnd()
    {
        var catalog = await CreateCatalogAsync();
        var source = await AddVideoAsync(catalog, "long audio", TimeSpan.FromSeconds(7),
            ["-f", "lavfi", "-t", "6", "-i", "testsrc=size=160x120:rate=10", "-f", "lavfi", "-t", "7", "-i", "sine=sample_rate=48000",
                "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanTrimAsync(source, TimeSpan.FromSeconds(1), source.Duration);
        var trimmed = Assert.Single(await editing.TrimAsync(source, TimeSpan.FromSeconds(1), source.Duration, "To the end"));

        Assert.Equal(TimeSpan.FromSeconds(6), plan.ActualEnd);
        Assert.InRange(trimmed.Duration.TotalSeconds, 4.9, 5.1);
    }

    [Fact]
    public async Task PreciseTrimEncodesTheRightFramesWhenTheSourceTimelineDoesNotStartAtZero()
    {
        var catalog = await CreateCatalogAsync();
        var fixture = await CreateFixtureAsync(catalog, "offset");
        var shifted = await AddFileAsync(catalog, "shifted", async path =>
        {
            var tracks = Enumerable.Range(0, 4).SelectMany(track => new[] { "--sync", $"{track}:1500" });
            Assert.Equal(0, (await new MediaProcess().RunAsync(_tools.MkvMerge, ["-q", "-o", path, .. tracks, catalog.GetVideoPath(fixture.RelativePath)],
                TimeSpan.FromSeconds(30))).ExitCode);
        });
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanTrimAsync(shifted, TimeSpan.FromSeconds(2.85), TimeSpan.FromSeconds(6.15));
        var trimmed = Assert.Single(await editing.TrimAsync(shifted, TimeSpan.FromSeconds(2.85), TimeSpan.FromSeconds(6.15), "Shifted"));

        Assert.False(plan.IsLossless);
        Assert.Equal(TimeSpan.FromSeconds(2.9), plan.ActualStart);
        // Absolute 2.9 s is content time 1.4 s, frame 14 of the unshifted fixture.
        var source = await GrayFramesAsync(catalog.GetVideoPath(fixture.RelativePath));
        var output = await GrayFramesAsync(catalog.GetVideoPath(trimmed.RelativePath));
        Assert.Equal(33, output.Count);
        Assert.InRange(MeanDifference(output[0], source[14]), 0, 4);
        Assert.Equal(source.Skip(20).Take(20).Select(Convert.ToHexString), output.Skip(6).Take(20).Select(Convert.ToHexString));
    }

    [Fact]
    public async Task PreciseTrimKeepsTheSourcesFrameTimesAcrossDroppedFrames()
    {
        var catalog = await CreateCatalogAsync();
        var source = await AddVideoAsync(catalog, "dropped frames", TimeSpan.FromSeconds(6),
            ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "6", "-vf", "select='not(between(n,75,78))'", "-fps_mode", "passthrough",
                "-c:v", "libx264", "-g", "25", "-pix_fmt", "yuv420p"]);
        var editing = new ClipEditingService(catalog, _tools);

        var trimmed = Assert.Single(await editing.TrimAsync(source, TimeSpan.FromSeconds(1.1), TimeSpan.FromSeconds(5.1), "Gap"));

        var expected = (await VideoTimesAsync(catalog.GetVideoPath(source.RelativePath)))
            .Where(time => time >= 1.1 - 0.0005 && time < 5.1 - 0.0005).Select(time => Math.Round(time - 1.12, 3)).ToArray();
        Assert.Equal(expected, (await VideoTimesAsync(catalog.GetVideoPath(trimmed.RelativePath))).Select(time => Math.Round(time, 3)));
    }

    [Fact]
    public async Task ClipsDifferingOnlyInNominalFrameDurationAreAppended()
    {
        var catalog = await CreateCatalogAsync();
        var first = await CreateFixtureAsync(catalog, "first");
        var second = await CreateFixtureAsync(catalog, "second");
        await new MediaProcess().RunAsync(_tools.MkvPropEdit, [catalog.GetVideoPath(second.RelativePath), "--edit", "track:v1", "--set", "default-duration=100000001"],
            TimeSpan.FromSeconds(30));
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanMergeAsync([first, second]);
        var output = Assert.Single(await editing.MergeAsync([first, second], "Appended"));

        Assert.True(plan.IsLossless);
        Assert.Equal(await TracksAsync(catalog, first), await TracksAsync(catalog, output));
    }

    [Fact]
    public async Task InterlacedClipsWithDifferentEncoderSettingsAreJoinedWithoutReencoding()
    {
        var catalog = await CreateCatalogAsync();
        LibraryVideo[] clips = [await CreateInterlacedAsync(catalog, "camera a", "2"), await CreateInterlacedAsync(catalog, "camera b", "4")];
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanMergeAsync(clips);
        var output = Assert.Single(await editing.MergeAsync(clips, "Tapes"));

        Assert.Equal(0, plan.ReencodedClips);
        var path = catalog.GetVideoPath(output.RelativePath);
        Assert.Equal((await DecodedFrameHashesAsync(catalog.GetVideoPath(clips[0].RelativePath)))
                .Concat(await DecodedFrameHashesAsync(catalog.GetVideoPath(clips[1].RelativePath))),
            await DecodedFrameHashesAsync(path));
        Assert.Contains("field_order=tt", (await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=field_order", "-of", "default=nw=1", path], TimeSpan.FromSeconds(30))).StandardOutput);
    }

    [Fact]
    public async Task ClipWhoseAudioOutlastsItsVideoIsJoinedWithoutAHoleInThePicture()
    {
        var catalog = await CreateCatalogAsync();
        var arguments = new[] { "-f", "lavfi", "-t", "4", "-i", "testsrc=size=160x120:rate=10", "-f", "lavfi", "-t", "5", "-i", "sine=sample_rate=48000",
            "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac" };
        var first = await AddVideoAsync(catalog, "first", TimeSpan.FromSeconds(5), arguments);
        var second = await AddVideoAsync(catalog, "second", TimeSpan.FromSeconds(5), arguments);

        var output = Assert.Single(await new ClipEditingService(catalog, _tools).MergeAsync([first, second], "No hole"));

        var times = await VideoTimesAsync(catalog.GetVideoPath(output.RelativePath));
        Assert.Equal(80, times.Length);
        Assert.InRange(times.Zip(times.Skip(1), (a, b) => b - a).Max(), 0.09, 0.11);
        var content = await new MatroskaMetadataService(catalog, _tools).ReadAsync(output);
        Assert.InRange(content.Chapters[1].Position.TotalSeconds, 3.99, 4.01);
    }

    [Fact]
    public async Task MergeSucceedsWhenAClipWasAlreadyDamagedNextToTheJoin()
    {
        var catalog = await CreateCatalogAsync();
        Task<LibraryVideo> TapeAsync(string name, bool damage) => AddFileAsync(catalog, name, async path =>
        {
            var tape = Path.Combine(_root, name + ".ts");
            await RunFfmpegAsync(["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "6", "-c:v", "mpeg2video", "-g", "12", "-f", "mpegts", tape]);
            if (damage) ImportTests.CorruptTransportPackets(tape, [0.95]);
            await RunFfmpegAsync(["-i", tape, "-c", "copy", path]);
        });
        var damaged = await TapeAsync("damaged", damage: true);
        var intact = await TapeAsync("intact", damage: false);
        Assert.NotEqual("", await DecodeErrorsAsync(catalog.GetVideoPath(damaged.RelativePath)));
        var warnings = new List<string>();

        var output = Assert.Single(await new ClipEditingService(catalog, _tools).MergeAsync([damaged, intact], "Tape",
            new InlineProgress(update => { if (update.Warning is { } warning) warnings.Add(warning); })));

        Assert.True(File.Exists(catalog.GetVideoPath(output.RelativePath)));
        Assert.True(warnings.Any(warning => warning.Contains("as in the original", StringComparison.Ordinal)), string.Join(" / ", warnings));
    }

    [Fact]
    public async Task CompositeMergeKeepsSubtitlesAndAttachmentsAndSmartCutKeepsTheTrackHeader()
    {
        var catalog = await CreateCatalogAsync();
        var first = await CreateFixtureAsync(catalog, "first");
        var second = await AddFileAsync(catalog, "second", async path =>
        {
            var fixture = await CreateFixtureAsync(catalog, "re-encoded");
            await RunFfmpegAsync(["-i", catalog.GetVideoPath(fixture.RelativePath), "-map", "0", "-c", "copy", "-c:v", "libx264", "-g", "10", "-crf", "30", path]);
        });
        var font = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(font, "kept");
        await new MediaProcess().RunAsync(_tools.MkvPropEdit, [catalog.GetVideoPath(second.RelativePath), "--add-attachment", font], TimeSpan.FromSeconds(30));
        await new MediaProcess().RunAsync(_tools.MkvPropEdit, [catalog.GetVideoPath(first.RelativePath), "--edit", "track:v1", "--set", "flag-default=0",
            "--set", "colour-range=1"], TimeSpan.FromSeconds(30));
        var editing = new ClipEditingService(catalog, _tools);

        var merged = Assert.Single(await editing.MergeAsync([first, second], "Composite"));
        var trimmed = Assert.Single(await editing.TrimAsync(first, TimeSpan.FromSeconds(1.35), TimeSpan.FromSeconds(4.65), "Header"));

        Assert.Equal(await TracksAsync(catalog, first), await TracksAsync(catalog, merged));
        var mergedInfo = await IdentifyAsync(catalog.GetVideoPath(merged.RelativePath));
        Assert.Contains(mergedInfo.GetProperty("attachments").EnumerateArray(), attachment => attachment.GetProperty("file_name").GetString() == "notes.txt");
        var video = (await IdentifyAsync(catalog.GetVideoPath(trimmed.RelativePath))).GetProperty("tracks")[0].GetProperty("properties");
        Assert.False(video.GetProperty("default_track").GetBoolean());
        Assert.Equal(1, video.GetProperty("color_range").GetInt32());
    }

    [Fact]
    public async Task OutputsGetRecordedDatesOnlyFromSourcesThatStateOneAndThumbnailsFromEveryClip()
    {
        var catalog = await CreateCatalogAsync();
        var first = await CreateSimpleAsync(catalog, "first", "64x48", 10, 3, audio: false);
        var second = await CreateSimpleAsync(catalog, "second", "64x48", 10, 3, audio: false);
        var metadata = new MatroskaMetadataService(catalog, _tools);
        var many = new List<VideoThumbnail>();
        for (var index = 0; index < 20; index++) many.Add(await metadata.CaptureThumbnailAsync(first, TimeSpan.FromSeconds(index * 0.1)));
        await metadata.SaveAsync(first, VideoMetadata.Empty with { Thumbnails = many });
        await metadata.SaveAsync(second, VideoMetadata.Empty with { Thumbnails = [await metadata.CaptureThumbnailAsync(second, TimeSpan.FromSeconds(1))] });
        var editing = new ClipEditingService(catalog, _tools);

        var merged = Assert.Single(await editing.MergeAsync([first, second], "Thumbnails"));

        var text = await metadata.ReadTextAsync(catalog.GetVideoPath(merged.RelativePath));
        Assert.Equal("", text.Metadata.RecordedAtText);
        Assert.Equal(20, text.Thumbnails.Count);
        Assert.Contains(text.Thumbnails, thumbnail => thumbnail.Position > TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ClipsOfOneSizeAtDifferentFrameRatesAreJoinedWithTheirOwnFrameTimes()
    {
        var catalog = await CreateCatalogAsync();
        LibraryVideo[] clips = [await CreateSimpleAsync(catalog, "pal", "160x120", 25, 4, audio: true),
            await CreateSimpleAsync(catalog, "ntsc", "160x120", 30, 2, audio: true)];

        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanMergeAsync(clips);
        var output = Assert.Single(await editing.MergeAsync(clips, "Mixed rates"));

        Assert.Contains("Clips keep their own frame rates (25 and 30 fps).", plan.Note, StringComparison.Ordinal);

        var path = catalog.GetVideoPath(output.RelativePath);
        Assert.Equal((await DecodedFrameHashesAsync(catalog.GetVideoPath(clips[0].RelativePath)))
            .Concat(await DecodedFrameHashesAsync(catalog.GetVideoPath(clips[1].RelativePath))), await DecodedFrameHashesAsync(path));
        var times = await VideoTimesAsync(path);
        Assert.InRange(times[99] - times[98], 0.039, 0.041);
        Assert.InRange(times[101] - times[100], 0.033, 0.034);
    }

    [Fact]
    public async Task ClipsWithDifferentH264ProfilesAreNotCopiedIntoOneStream()
    {
        var catalog = await CreateCatalogAsync();
        Task<LibraryVideo> ProfileAsync(string profile, int seconds) => AddVideoAsync(catalog, profile, TimeSpan.FromSeconds(seconds),
            ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", seconds.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-profile:v", profile, "-pix_fmt", "yuv420p"]);
        LibraryVideo[] clips = [await ProfileAsync("high", 3), await ProfileAsync("baseline", 2)];

        var plan = await new ClipEditingService(catalog, _tools).PlanMergeAsync(clips);

        Assert.Equal(1, plan.ReencodedClips);
    }

    [Fact]
    public async Task InvalidBoundariesPublishNothing()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateFixtureAsync(catalog, "bounds");
        var editing = new ClipEditingService(catalog, _tools);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.TrimAsync(source, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.SplitAsync(source, TimeSpan.Zero, ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => editing.SplitAsync(source, TimeSpan.FromSeconds(6), ""));
        await Assert.ThrowsAsync<ArgumentException>(() => editing.MergeAsync([source], ""));
        Assert.Single(await catalog.ListAsync());
        Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task CancellingSplitAfterFirstOutputIsPreparedLeavesNoPartialBatch()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateFixtureAsync(catalog, "cancel");
        var before = await HashAsync(catalog, source);
        using var cancellation = new CancellationTokenSource();
        var prepared = 0;
        var progress = new InlineProgress(update =>
        {
            if (update.Stage == "Writing descriptions, chapters and thumbnails" && update.Warning is null && ++prepared == 2) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClipEditingService(catalog, _tools)
            .SplitAsync(source, TimeSpan.FromSeconds(3), "Cancel split", progress, cancellation.Token));

        Assert.Equal(2, prepared);
        Assert.Single(await catalog.ListAsync());
        Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
        Assert.Equal(before, await HashAsync(catalog, source));
        AssertNoScratch(catalog);
    }

    [Fact]
    public async Task SplitRegistrationFailureRollsBackAllRowsAndPublishedFiles()
    {
        var catalog = await CreateCatalogAsync();
        var source = await CreateFixtureAsync(catalog, "rollback");
        await using (var connection = new SqliteConnection($"Data Source={catalog.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_second_part BEFORE INSERT ON videos WHEN new.display_name LIKE '%part 2' BEGIN SELECT RAISE(ABORT,'fail second output'); END";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<SqliteException>(() => new ClipEditingService(catalog, _tools).SplitAsync(source, TimeSpan.FromSeconds(3), "Rollback"));

        Assert.Single(await catalog.ListAsync());
        Assert.Single(Directory.GetFiles(catalog.Paths.VideosDirectory));
        AssertNoScratch(catalog);
    }

    private static void AssertNoScratch(LibraryCatalog catalog) =>
        Assert.Empty(Directory.Exists(catalog.Paths.ScratchDirectory) ? Directory.GetFileSystemEntries(catalog.Paths.ScratchDirectory, "edit-*") : []);

    private async Task<LibraryCatalog> CreateCatalogAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        await catalog.InitializeAsync();
        return catalog;
    }

    /// <summary>6 s of 160x120 at 10 fps with a keyframe every second, two languages of audio and a subtitle track.</summary>
    private async Task<LibraryVideo> CreateFixtureAsync(LibraryCatalog catalog, string name)
    {
        Directory.CreateDirectory(_root);
        var subtitles = Path.Combine(_root, "captions.srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:00,500 --> 00:00:02,000\nHello\n\n2\n00:00:03,000 --> 00:00:05,000\nWorld\n");
        return await AddVideoAsync(catalog, name, TimeSpan.FromSeconds(6),
        [
            "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000", "-i", subtitles, "-t", "6",
            "-map", "0", "-map", "1", "-map", "2", "-map", "3", "-c:v", "libx264", "-g", "10", "-pix_fmt", "yuv420p", "-c:a", "aac", "-c:s", "srt",
            "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=fra", "-metadata:s:s:0", "language=deu",
        ]);
    }

    private Task<LibraryVideo> CreateSimpleAsync(LibraryCatalog catalog, string name, string dimensions, int fps, int seconds, bool audio)
    {
        var arguments = new List<string> { "-f", "lavfi", "-i", $"testsrc=size={dimensions}:rate={fps}" };
        if (audio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100"]);
        arguments.AddRange(["-t", seconds.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-g", fps.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p"]);
        if (audio) arguments.AddRange(["-c:a", "aac"]);
        return AddVideoAsync(catalog, name, TimeSpan.FromSeconds(seconds), arguments);
    }

    private async Task<LibraryVideo> CreateInterlacedAsync(LibraryCatalog catalog, string name, string references) =>
        await AddVideoAsync(catalog, name, TimeSpan.FromSeconds(2), ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "2",
            "-vf", "setfield=tff", "-flags", "+ilme+ildct", "-c:v", "libx264", "-refs", references, "-g", "25", "-pix_fmt", "yuv420p"]);

    private async Task<LibraryVideo> AddFileAsync(LibraryCatalog catalog, string name, Func<string, Task> create)
    {
        var id = Guid.NewGuid();
        var relative = LibraryPaths.VideoRelativePath(id);
        Directory.CreateDirectory(_root);
        await create(catalog.GetVideoPath(relative));
        var video = new LibraryVideo(id, relative, name, TimeSpan.FromSeconds(6), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await catalog.AddAsync(video);
        return video;
    }

    private async Task RunFfmpegAsync(IEnumerable<string> arguments)
    {
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-hide_banner", "-v", "error", "-y", .. arguments], TimeSpan.FromSeconds(30));
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    private async Task<IReadOnlyList<byte[]>> GrayFramesAsync(string path)
    {
        var raw = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".gray");
        await RunFfmpegAsync(["-i", path, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "gray", raw]);
        return (await File.ReadAllBytesAsync(raw)).Chunk(160 * 120).ToArray();
    }

    private static double MeanDifference(byte[] first, byte[] second) => first.Zip(second, (a, b) => Math.Abs(a - b)).Average();

    private async Task<double[]> VideoTimesAsync(string path) =>
        (await new MediaProcess().RunAsync(_tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path],
            TimeSpan.FromSeconds(30))).StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => double.Parse(line.TrimEnd(','), CultureInfo.InvariantCulture)).Order().ToArray();

    private async Task<JsonElement> IdentifyAsync(string path) =>
        JsonDocument.Parse((await new MediaProcess().RunAsync(_tools.MkvMerge, ["-J", path], TimeSpan.FromSeconds(30))).StandardOutput).RootElement;

    private async Task<LibraryVideo> AddVideoAsync(LibraryCatalog catalog, string name, TimeSpan duration, IEnumerable<string> arguments)
    {
        var id = Guid.NewGuid();
        var relative = LibraryPaths.VideoRelativePath(id);
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-v", "error", "-n", .. arguments, catalog.GetVideoPath(relative)], TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        var video = new LibraryVideo(id, relative, name, duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await catalog.AddAsync(video);
        return video;
    }

    private static async Task<byte[]> HashAsync(LibraryCatalog catalog, LibraryVideo video) =>
        SHA256.HashData(await File.ReadAllBytesAsync(catalog.GetVideoPath(video.RelativePath)));

    /// <summary>Video packet payload hashes in file order, for packets presented in [from, to).</summary>
    private async Task<string[]> PacketHashesAsync(string path, TimeSpan from, TimeSpan to)
    {
        var result = await new MediaProcess().RunAsync(_tools.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_data_hash", "MD5",
            "-show_entries", "packet=pts_time,data_hash", "-of", "csv=p=0", path], TimeSpan.FromSeconds(30));
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(','))
            .Where(fields => TimeSpan.FromSeconds(double.Parse(fields[0], CultureInfo.InvariantCulture)) is var pts && pts >= from - TimeSpan.FromMilliseconds(1) && pts < to - TimeSpan.FromMilliseconds(1))
            .Select(fields => fields[1]).ToArray();
    }

    private async Task<IReadOnlyList<string>> DecodedFrameHashesAsync(string path)
    {
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"], TimeSpan.FromSeconds(30));
        return result.StandardOutput.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#')).Select(line => line.Split(',')[^1].Trim()).ToArray();
    }

    private async Task<string> DecodeErrorsAsync(string path) =>
        (await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-v", "error", "-i", path, "-f", "null", "-"], TimeSpan.FromSeconds(30))).StandardError.Trim();

    private async Task<string[]> TracksAsync(LibraryCatalog catalog, LibraryVideo video)
    {
        var result = await new MediaProcess().RunAsync(_tools.MkvMerge, ["-J", catalog.GetVideoPath(video.RelativePath)], TimeSpan.FromSeconds(30));
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("tracks").EnumerateArray()
            .Select(track => $"{track.GetProperty("type").GetString()} {track.GetProperty("codec").GetString()} {track.GetProperty("properties").GetProperty("language").GetString()}")
            .ToArray();
    }

    private async Task<string> CodecPrivateAsync(string path)
    {
        var result = await new MediaProcess().RunAsync(_tools.MkvMerge, ["-J", path], TimeSpan.FromSeconds(30));
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.GetProperty("tracks").EnumerateArray().First(track => track.GetProperty("type").GetString() == "video")
            .GetProperty("properties").GetProperty("codec_private_data").GetString()!;
    }

    private async Task<JsonElement[]> StreamsAsync(string path)
    {
        var probe = await new MediaProcess().RunAsync(_tools.Ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,width,height,r_frame_rate", "-of", "json", path], TimeSpan.FromSeconds(30));
        return JsonDocument.Parse(probe.StandardOutput).RootElement.GetProperty("streams").EnumerateArray().ToArray();
    }

    private async Task<int> CountGlobalTitlesAsync(string path)
    {
        var tags = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".xml");
        await new MediaProcess().RunAsync(_tools.MkvExtract, [path, "tags", tags], TimeSpan.FromSeconds(30));
        var xml = System.Xml.Linq.XDocument.Load(tags);
        return xml.Descendants("Tag")
            .Where(tag => tag.Element("Targets")?.Element("TrackUID") is null && tag.Element("Targets")?.Element("ChapterUID") is null)
            .SelectMany(tag => tag.Elements("Simple")).Count(simple => simple.Element("Name")?.Value == "TITLE");
    }

    private sealed class InlineProgress(Action<ClipEditProgress> report) : IProgress<ClipEditProgress>
    {
        public void Report(ClipEditProgress value) => report(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
