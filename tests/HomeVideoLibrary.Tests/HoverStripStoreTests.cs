using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

public sealed class HoverStripStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-strips-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMatroska _frames = new();
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
    public async Task PruneKeepsOnlyStripsOfLiveVideosInTheCurrentFormat()
    {
        var store = new HoverStripStore(Paths, _frames);
        var (kept, keptPath) = await VideoAsync("eeee");
        var (gone, gonePath) = await VideoAsync("ffff");
        var keep = await store.GetOrBuildAsync(kept, keptPath, default);
        var drop = await store.GetOrBuildAsync(gone, gonePath, default);
        var oldFormat = Path.Combine(Paths.StripDirectory, $"{kept.Id:N}.eeee.v0.jpg");
        await File.WriteAllTextAsync(oldFormat, "old");

        store.Prune(new HashSet<Guid> { kept.Id });

        Assert.Equal(keep, Assert.Single(Directory.GetFiles(Paths.StripDirectory)));
        Assert.False(File.Exists(drop));
    }

    [Fact]
    public async Task RealStripIsAFiveByFourSpriteThatAMetadataSaveKeeps()
    {
        var tools = MediaTools.Discover();
        var catalog = await CreateCatalogAsync(_root);
        Directory.CreateDirectory(Path.Combine(_root, "sources"));
        var source = Path.Combine(_root, "sources", "testsrc2.mkv");
        var generated = await new MediaProcess().RunAsync(tools.Ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25",
            "-t", "8", "-g", "25", "-c:v", "libx264", "-pix_fmt", "yuv420p", source], TimeSpan.FromSeconds(60));
        Assert.Equal(0, generated.ExitCode);
        var video = (await new VideoImporter(catalog, tools).ImportAsync(source)).Video;
        var service = new MatroskaMetadataService(catalog, tools);
        var model = new LibraryModel(catalog, service);
        var record = await model.UpsertFromFileAsync(video);
        var store = new HoverStripStore(catalog.Paths, service);

        var strip = await store.GetOrBuildAsync(record, model.GetVideoPath(record), default);

        var probe = await new MediaProcess().RunAsync(tools.Ffprobe, ["-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0", strip!],
            TimeSpan.FromSeconds(30));
        Assert.Equal($"{5 * 416},{4 * 234}", probe.StandardOutput.Trim());
        Assert.InRange(new FileInfo(strip!).Length, 10_000, 1_500_000);

        await service.SaveAsync(video, record.ToMetadata()! with { Title = "Renamed", Description = "Saved in place" });
        var saved = await model.UpsertFromFileAsync(video);
        Assert.NotEqual(record.Stamp, saved.Stamp);
        Assert.Equal(strip, store.TryGet(saved));
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
