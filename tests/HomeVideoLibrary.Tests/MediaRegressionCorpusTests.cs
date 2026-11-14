using System.Globalization;
using System.Text.Json;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

/// <summary>Pins every media behaviour earlier review rounds fixed, on media generated here with ffmpeg, so a later
/// change to the integrity checks or the edit paths cannot silently undo one. Clean media must report no damage, even
/// when decoded from a seek into the middle; every kind of damaged media must report some; merges that can be lossless
/// must stay lossless; unusual but valid Matroska must import.</summary>
public sealed class MediaRegressionCorpusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-corpus-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = MediaTools.Discover();

    public static TheoryData<string, string[]> CleanMedia => new()
    {
        // Open GOPs: a seek lands on a recovery-point keyframe whose leading pictures the decoder discards with error text.
        { "open-gop-h264.mkv", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "10", "-c:v", "libx264", "-x264-params", "open-gop=1:keyint=25", "-pix_fmt", "yuv420p"] },
        { "open-gop-hevc.mkv", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "10", "-c:v", "libx265", "-x265-params", "keyint=25:log-level=error", "-pix_fmt", "yuv420p"] },
        { "variable-rate.mkv", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=30", "-t", "10", "-vf", "select='not(between(n,60,75))'", "-fps_mode", "vfr", "-c:v", "libx264", "-pix_fmt", "yuv420p"] },
        { "b-pyramid.mkv", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "10", "-c:v", "libx264", "-x264-params", "bframes=8:b-pyramid=normal", "-pix_fmt", "yuv420p"] },
        { "interlaced.ts", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "10", "-vf", "setfield=tff", "-flags", "+ilme+ildct", "-c:v", "mpeg2video", "-c:a", "mp2"] },
        { "mjpeg-pcm.avi", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "10", "-c:v", "mjpeg", "-c:a", "pcm_s16le"] },
        { "late-start.mkv", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "10", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-output_ts_offset", "1.5"] },
    };

    [Theory]
    [MemberData(nameof(CleanMedia))]
    public async Task CleanMediaReportsNoDamageInFullOrFromMidFileSeeks(string name, string[] arguments)
    {
        var path = await CreateAsync(name, arguments);
        var validator = new MediaValidator(_tools, new MediaProcess());

        var full = await validator.ValidateDecodedAsync(path, TimeSpan.FromSeconds(10), DecodeTolerance.ForImport(isMpegStream: name.EndsWith(".ts", StringComparison.Ordinal)));
        var windows = new List<int>();
        foreach (var seconds in new[] { 2.7, 3.9, 5.2, 7.7 })
            windows.Add((await validator.DecodeWindowAsync(new DecodeWindow(path, TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(2)))).ErrorCount);

        Assert.Equal(0, full.ErrorCount);
        Assert.All(windows, errors => Assert.Equal(0, errors));
    }

    public static TheoryData<string, string> DamagedMedia => new()
    {
        { "wrecked-cluster.mkv", "cluster" },
        { "random-bytes.mkv", "random" },
        { "zeroed.mkv", "zero" },
        { "garbled.ts", "transport" },
        { "audio-only.mka", "random" },
    };

    [Theory]
    [MemberData(nameof(DamagedMedia))]
    public async Task EveryKindOfDamageIsReported(string name, string damage)
    {
        string[] arguments = name.EndsWith(".mka", StringComparison.Ordinal)
            ? ["-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "20", "-c:a", "aac"]
            : ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "20",
                "-c:v", name.EndsWith(".ts", StringComparison.Ordinal) ? "mpeg2video" : "libx264", "-g", "25", "-pix_fmt", "yuv420p",
                "-c:a", name.EndsWith(".ts", StringComparison.Ordinal) ? "mp2" : "aac"];
        var path = await CreateAsync(name, arguments);
        var bytes = await File.ReadAllBytesAsync(path);
        switch (damage)
        {
            case "cluster":
                var cluster = bytes.AsSpan(bytes.Length / 2).IndexOf(new byte[] { 0x1F, 0x43, 0xB6, 0x75 }) + bytes.Length / 2;
                new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 }.CopyTo(bytes, cluster + 4);
                break;
            case "random":
                new Random(5).NextBytes(bytes.AsSpan(bytes.Length / 2, 4000));
                break;
            case "zero":
                bytes.AsSpan(bytes.Length / 2, 4000).Clear();
                break;
        }
        await File.WriteAllBytesAsync(path, bytes);
        if (damage == "transport") ImportTests.CorruptTransportPackets(path);

        var report = await new MediaValidator(_tools, new MediaProcess())
            .ValidateDecodedAsync(path, TimeSpan.FromSeconds(20), new DecodeTolerance(TimeSpan.FromSeconds(20), 1));

        Assert.True(report.ErrorCount > 0, $"{name} was reported as intact.");
    }

    [Fact]
    public async Task ClipsDifferingOnlyInLevelAndFrameRateMergeLosslessly()
    {
        var catalog = await CreateCatalogAsync();
        var thirty = await AddAsync(catalog, "thirty", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=30", "-t", "3", "-c:v", "libx264", "-level", "4.0", "-pix_fmt", "yuv420p"]);
        var sixty = await AddAsync(catalog, "sixty", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=60", "-t", "2", "-c:v", "libx264", "-level", "4.2", "-pix_fmt", "yuv420p"]);
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanMergeAsync([thirty, sixty]);
        var output = Assert.Single(await editing.MergeAsync([thirty, sixty], "Levels"));

        Assert.True(plan.IsLossless, plan.Note);
        Assert.Contains("Clips keep their own frame rates (30 and 60 fps).", plan.Note, StringComparison.Ordinal);
        Assert.Equal((await FramesAsync(catalog.GetVideoPath(thirty.RelativePath))).Concat(await FramesAsync(catalog.GetVideoPath(sixty.RelativePath))),
            await FramesAsync(catalog.GetVideoPath(output.RelativePath)));
    }

    [Fact]
    public async Task ClipsAtTwentyFiveAndThirtyFramesPerSecondMergeLosslessly()
    {
        var catalog = await CreateCatalogAsync();
        var pal = await AddAsync(catalog, "pal", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=25", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var ntsc = await AddAsync(catalog, "ntsc", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=30", "-t", "2", "-c:v", "libx264", "-pix_fmt", "yuv420p"]);
        var editing = new ClipEditingService(catalog, _tools);

        var plan = await editing.PlanMergeAsync([pal, ntsc]);
        var output = Assert.Single(await editing.MergeAsync([pal, ntsc], "Rates"));

        Assert.True(plan.IsLossless, plan.Note);
        Assert.Equal((await FramesAsync(catalog.GetVideoPath(pal.RelativePath))).Concat(await FramesAsync(catalog.GetVideoPath(ntsc.RelativePath))),
            await FramesAsync(catalog.GetVideoPath(output.RelativePath)));
    }

    [Fact]
    public async Task LateStartingAndDurationLessMatroskaImport()
    {
        var late = await CreateAsync("late.mkv", ["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-output_ts_offset", "1.5"]);
        var live = await CreateAsync("live.mkv", ["-f", "lavfi", "-i", "testsrc=size=64x48:rate=10", "-t", "3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-live", "1"]);
        var catalog = await CreateCatalogAsync();
        var importer = new VideoImporter(catalog, _tools);

        var lateResult = await importer.ImportAsync(late);
        var liveResult = await importer.ImportAsync(live);

        var start = await new MediaProcess().RunAsync(_tools.Ffprobe, ["-v", "error", "-show_entries", "format=start_time", "-of", "csv=p=0",
            catalog.GetVideoPath(lateResult.Video!.RelativePath)], TimeSpan.FromSeconds(30));
        Assert.InRange(double.Parse(start.StandardOutput.Trim(), CultureInfo.InvariantCulture), 0, 0.1);
        Assert.InRange(liveResult.Video!.Duration.TotalSeconds, 2.9, 3.1);
        Assert.DoesNotContain(lateResult.Warnings.Concat(liveResult.Warnings), warning => warning.Contains("damaged", StringComparison.Ordinal));
    }

    private async Task<string> CreateAsync(string name, IEnumerable<string> arguments)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-hide_banner", "-v", "error", "-y", .. arguments, path], TimeSpan.FromMinutes(1));
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    private async Task<LibraryCatalog> CreateCatalogAsync()
    {
        var catalog = new LibraryCatalog(new LibraryPaths(Path.Combine(_root, "library")));
        await catalog.InitializeAsync();
        return catalog;
    }

    private async Task<LibraryVideo> AddAsync(LibraryCatalog catalog, string name, IEnumerable<string> arguments)
    {
        var id = Guid.NewGuid();
        var video = new LibraryVideo(id, LibraryPaths.VideoRelativePath(id), name, TimeSpan.FromSeconds(3), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-hide_banner", "-v", "error", .. arguments, catalog.GetVideoPath(video.RelativePath)],
            TimeSpan.FromMinutes(1));
        Assert.True(result.ExitCode == 0, result.StandardError);
        await catalog.AddAsync(video);
        return video;
    }

    private async Task<string[]> FramesAsync(string path) =>
        (await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"],
            TimeSpan.FromMinutes(1))).StandardOutput.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Select(line => line.Split(',')[^1].Trim()).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
