using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class HoverStripStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-strips-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _frames = new();
    private readonly MediaTools _tools = MediaTools.Discover();
    private LibraryPaths Paths => new(_root);

    [Fact]
    public void SlotsAreTheMiddlesOfEachTwentiethAndTilesFollowTheFiveByFourGrid()
    {
        var duration = TimeSpan.FromSeconds(100);
        Assert.Equal(TimeSpan.FromSeconds(2.5), HoverStripStore.SlotTime(0, duration));
        Assert.Equal(TimeSpan.FromSeconds(37.5), HoverStripStore.SlotTime(7, duration));
        Assert.Equal(TimeSpan.FromSeconds(97.5), HoverStripStore.SlotTime(19, duration));
        Assert.Throws<ArgumentOutOfRangeException>(() => HoverStripStore.SlotTime(20, duration));

        Assert.Equal((0, 0, 416, 234), HoverStripStore.Tile(0, 2080, 936));
        Assert.Equal((832, 234, 416, 234), HoverStripStore.Tile(7, 2080, 936));
        Assert.Equal((1664, 702, 416, 234), HoverStripStore.Tile(19, 2080, 936));
    }

    [Fact]
    public async Task BuildsOneStripNamedForTheMediaFromTheSlotMiddlesAndReusesIt()
    {
        var (record, path) = await VideoAsync("abc123");
        var store = new HoverStripStore(Paths, _frames);
        Assert.Null(store.TryGet(record));

        var strip = await store.GetOrBuildAsync(record, path, default);

        Assert.Equal(Path.Combine(Paths.StripDirectory, $"{record.Id:N}.abc123.v1.jpg"), strip);
        Assert.Equal(strip, store.TryGet(record));
        Assert.Equal(Enumerable.Range(0, HoverStripStore.Slots).Select(slot => $"keyframe {HoverStripStore.SlotTime(slot, record.Duration)}"),
            await File.ReadAllLinesAsync(strip!));
        Assert.Equal(strip, await store.GetOrBuildAsync(record, path, default));
        Assert.Equal(HoverStripStore.Slots, _frames.Keyframes);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Paths.ScratchDirectory));
    }

    [Fact]
    public async Task AMetadataSaveKeepsTheStripAndNewMediaReplacesIt()
    {
        var (record, path) = await VideoAsync("aaaa");
        var store = new HoverStripStore(Paths, _frames);
        var strip = await store.GetOrBuildAsync(record, path, default);

        await File.AppendAllTextAsync(path, " retitled");
        var saved = record with { Title = "Retitled", Stamp = FileStamp.TryRead(path) };
        Assert.Equal(strip, store.TryGet(saved));
        Assert.Equal(strip, await store.GetOrBuildAsync(saved, path, default));
        Assert.Equal(HoverStripStore.Slots, _frames.Keyframes);

        var replaced = saved with { MediaIdentity = "bbbb" };
        Assert.Null(store.TryGet(replaced));
        var rebuilt = await store.GetOrBuildAsync(replaced, path, default);
        Assert.NotEqual(strip, rebuilt);
        Assert.False(File.Exists(strip));
        Assert.Equal(rebuilt, Assert.Single(Directory.GetFiles(Paths.StripDirectory)));
    }

    [Fact]
    public async Task AFileThatChangesDuringTheBuildOrNoLongerMatchesTheRecordLeavesNoStrip()
    {
        var (record, path) = await VideoAsync("cccc");
        var store = new HoverStripStore(Paths, _frames);
        _frames.BeforeKeyframe = async (mkv, token) =>
        {
            _frames.BeforeKeyframe = null;
            await File.AppendAllTextAsync(mkv, " replaced", token);
        };

        Assert.Null(await store.GetOrBuildAsync(record, path, default));
        Assert.False(Directory.Exists(Paths.StripDirectory) && Directory.EnumerateFiles(Paths.StripDirectory).Any());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Paths.ScratchDirectory));

        var captured = _frames.Keyframes;
        Assert.Null(await store.GetOrBuildAsync(record, path, default)); // The record describes the file before the change.
        Assert.Equal(captured, _frames.Keyframes);
        Assert.NotNull(await store.GetOrBuildAsync(record with { Stamp = FileStamp.TryRead(path) }, path, default));
    }

    [Fact]
    public async Task VideosWithoutAKnownIdentityOrDurationHaveNoStrip()
    {
        var (record, path) = await VideoAsync("dddd");
        var store = new HoverStripStore(Paths, _frames);

        Assert.False(HoverStripStore.CanBuild(record with { MediaIdentity = null }));
        Assert.False(HoverStripStore.CanBuild(record with { MediaIdentity = "../x" }));
        Assert.False(HoverStripStore.CanBuild(record with { Duration = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetOrBuildAsync(record with { MediaIdentity = null }, path, default));
        Assert.Null(store.TryGet(record with { MediaIdentity = null }));
    }

    [Fact]
    public async Task LoadingIndexesUsableStripsOfLiveVideosAndDeletesTheRest()
    {
        var builder = new HoverStripStore(Paths, _frames);
        var (kept, keptPath) = await VideoAsync("eeee");
        var (gone, gonePath) = await VideoAsync("ffff");
        var (emptied, emptiedPath) = await VideoAsync("0000");
        var keep = await builder.GetOrBuildAsync(kept, keptPath, default);
        await builder.GetOrBuildAsync(gone, gonePath, default);
        await File.WriteAllBytesAsync((await builder.GetOrBuildAsync(emptied, emptiedPath, default))!, []);
        await File.WriteAllTextAsync(Path.Combine(Paths.StripDirectory, $"{kept.Id:N}.eeee.v0.jpg"), "old format");
        var store = new HoverStripStore(Paths, _frames);
        Assert.Null(store.TryGet(kept));

        await store.LoadAsync(new HashSet<Guid> { kept.Id, emptied.Id });

        Assert.Equal(keep, store.TryGet(kept));
        Assert.Null(store.TryGet(emptied));
        Assert.Equal(keep, Assert.Single(Directory.GetFiles(Paths.StripDirectory)));
        await new HoverStripStore(new LibraryPaths(Path.Combine(_root, "never-created")), _frames).LoadAsync(new HashSet<Guid>());
    }

    [Fact]
    public async Task AnInvalidatedStripIsForgottenAndBuiltAgain()
    {
        var (record, path) = await VideoAsync("abab");
        var store = new HoverStripStore(Paths, _frames);
        var strip = await store.GetOrBuildAsync(record, path, default);

        store.Invalidate(strip!);

        Assert.Null(store.TryGet(record));
        Assert.False(File.Exists(strip));
        Assert.Equal(strip, await store.GetOrBuildAsync(record, path, default));
        Assert.Equal(2 * HoverStripStore.Slots, _frames.Keyframes);
    }

    [Fact]
    public async Task CancellingWhileTilingLeavesNoFiles()
    {
        var (record, path) = await VideoAsync("cdcd");
        var store = new HoverStripStore(Paths, _frames);
        using var cancellation = new CancellationTokenSource();
        _frames.BeforeTile = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetOrBuildAsync(record, path, cancellation.Token));

        Assert.Null(store.TryGet(record));
        Assert.False(Directory.Exists(Paths.StripDirectory) && Directory.EnumerateFileSystemEntries(Paths.StripDirectory).Any());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Paths.ScratchDirectory));
    }

    [Fact]
    public async Task RealStripIsAFiveByFourSpriteOfDistinctFramesThatAMetadataSaveKeeps()
    {
        var catalog = await CreateCatalogAsync(_root);
        // A keyframe every 0.2 s, so each 0.4 s slot lands on its own one.
        var video = (await new VideoImporter(catalog, _tools).ImportAsync(
            await GenerateAsync("testsrc2.mkv", "testsrc2=size=640x360:rate=25", 8, keyframeEvery: 5))).Video;
        var service = new MatroskaMetadataService(catalog, _tools);
        var model = new LibraryModel(catalog, service);
        var record = await model.UpsertFromFileAsync(video);
        var store = new HoverStripStore(catalog.Paths, service);

        var strip = (await store.GetOrBuildAsync(record, model.GetVideoPath(record), default))!;

        var (width, height) = (5 * 416, 4 * 234);
        Assert.Equal($"{width},{height}", (await ToolAsync(_tools.Ffprobe, ["-v", "error", "-show_entries", "stream=width,height",
            "-of", "csv=p=0", strip])).StandardOutput.Trim());
        Assert.InRange(new FileInfo(strip).Length, 10_000, 1_500_000);
        var gray = Path.Combine(_root, "strip.gray");
        await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-i", strip, "-f", "rawvideo", "-pix_fmt", "gray", gray]);
        var pixels = await File.ReadAllBytesAsync(gray);
        var tiles = Enumerable.Range(0, HoverStripStore.Slots).Select(slot =>
        {
            var (x, y, w, h) = HoverStripStore.Tile(slot, width, height);
            return Enumerable.Range(y, h).SelectMany(row => pixels.AsSpan(row * width + x, w).ToArray()).ToArray();
        }).ToArray();
        Assert.All(tiles, tile => Assert.True(tile.Average(value => (double)value) > 40, "a tile is black"));
        for (var a = 0; a < tiles.Length; a++)
            for (var b = a + 1; b < tiles.Length; b++)
                Assert.True(tiles[a].Zip(tiles[b], (left, right) => Math.Abs(left - right)).Average() > 1, $"slots {a} and {b} show the same frame");

        await service.SaveAsync(video, record.ToMetadata()! with { Title = "Renamed", Description = "Saved in place" });
        var saved = await model.UpsertFromFileAsync(video);
        Assert.NotEqual(record.Stamp, saved.Stamp);
        Assert.Equal(strip, store.TryGet(saved));
    }

    [Fact]
    public async Task RealShortFileWithOneKeyframeStillGetsAStrip()
    {
        var path = await GenerateAsync("one-keyframe.mkv", "testsrc2=size=320x240:rate=25", 1, keyframeEvery: 1000);
        var record = await ReadAsync(path);

        Assert.NotNull(await new HoverStripStore(Paths, new MatroskaMetadataService(await CreateCatalogAsync(_root), _tools))
            .GetOrBuildAsync(record, path, default));
    }

    [Fact]
    public async Task RealAudioOnlyFileFailsAsUnreadableMediaAndLeavesNoFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        var path = Path.Combine(_root, "sources", "audio.mkv");
        await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "sine=duration=2", "-c:a", "aac", path]);
        var record = await ReadAsync(path);
        var store = new HoverStripStore(Paths, new MatroskaMetadataService(await CreateCatalogAsync(_root), _tools));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetOrBuildAsync(record, path, default));

        Assert.False(Directory.Exists(Paths.StripDirectory) && Directory.EnumerateFileSystemEntries(Paths.StripDirectory).Any());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Paths.ScratchDirectory));
    }

    private async Task<string> GenerateAsync(string name, string source, int seconds, int keyframeEvery)
    {
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        var path = Path.Combine(_root, "sources", name);
        await ToolAsync(_tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", source, "-t", $"{seconds}", "-g", $"{keyframeEvery}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", path]);
        return path;
    }

    /// <summary>A record of the file as the library would read it.</summary>
    private async Task<VideoRecord> ReadAsync(string path)
    {
        var text = await new MatroskaMetadataService(new LibraryCatalog(Paths), _tools).ReadTextAsync(path);
        return Record("real").WithContent(text, FileStamp.TryRead(path)!.Value);
    }

    private async Task<MediaProcessResult> ToolAsync(string tool, string[] arguments)
    {
        var result = await new MediaProcess().RunAsync(tool, arguments, TimeSpan.FromSeconds(60));
        Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
        return result;
    }

    private async Task<(VideoRecord Record, string Path)> VideoAsync(string identity)
    {
        var record = Record("video") with { MediaIdentity = identity };
        var path = Path.Combine(Paths.VideosDirectory, $"{record.Id:N}.mkv");
        Directory.CreateDirectory(Paths.VideosDirectory);
        Directory.CreateDirectory(Paths.ScratchDirectory);
        await File.WriteAllTextAsync(path, "video " + identity);
        return (record with { Stamp = FileStamp.TryRead(path) }, path);
    }

    public void Dispose()
    {
        DeleteDirectory(_root);
        DeleteDirectory(_frames.OutputDirectory);
    }
}
