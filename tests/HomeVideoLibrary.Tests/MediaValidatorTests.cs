using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class MediaValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-validator-" + Guid.NewGuid().ToString("N"));
    private readonly MediaTools _tools = MediaTools.Discover();

    [Fact]
    public async Task LenientDecodePassesDamagedFootageAndCountsItsErrors()
    {
        var path = await CreateAsync("tape.ts", ["-c:v", "mpeg2video", "-g", "12", "-c:a", "mp2", "-f", "mpegts"]);
        ImportTests.CorruptTransportPackets(path);

        var report = await new MediaValidator(_tools, new MediaProcess())
            .ValidateDecodedAsync(path, TimeSpan.FromSeconds(6), DecodeTolerance.ForImport(isMpegStream: true));

        Assert.True(report.ErrorCount > 0);
        Assert.InRange(report.Decoded.TotalSeconds, 5.5, 6.5);
    }

    [Fact]
    public async Task TruncatedFileFailsOnTheDecodedShortfall()
    {
        var path = await CreateAsync("truncated.mkv", ["-c:v", "libx264", "-c:a", "aac"]);
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length / 2);

        await Assert.ThrowsAsync<InvalidDataException>(() => new MediaValidator(_tools, new MediaProcess())
            .ValidateDecodedAsync(path, TimeSpan.FromSeconds(6), DecodeTolerance.ForImport(isMpegStream: false)));
    }

    [Fact]
    public void EditToleranceIsTwoFramesOrAQuarterSecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.25), DecodeTolerance.ForEdit(TimeSpan.FromSeconds(0.04)).For(TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromSeconds(0.4), DecodeTolerance.ForEdit(TimeSpan.FromSeconds(0.2)).For(TimeSpan.FromHours(2)));
        Assert.Equal(TimeSpan.FromSeconds(36), DecodeTolerance.ForImport(isMpegStream: false).For(TimeSpan.FromHours(2)));
    }

    // Excerpts of ffmpeg 9.0.2 `-v level+verbose` decode logs of the named files.
    private const string GlitchedTransportStream = """
        [in#0/mpegts @ 0xad7400000] [warning] PES packet size mismatch
        [in#0/mpegts @ 0xad6810000] [warning] corrupt input packet in stream 1
        [mp2 @ 0x929025f80] [error] Header missing
        [aist#0:1/mp2 @ 0x928c24300] [dec:mp2 @ 0x928c2c3c0] [error] Error submitting packet to decoder: Invalid data found when processing input
        [mpeg2video @ 0x929025500] [error] ac-tex damaged at 2 7
        [mpeg2video @ 0x929025500] [error] Warning MVs not available
        [vist#0:0/mpeg2video @ 0xad7000480] [dec:mpeg2video @ 0xad6810280] [warning] corrupt decoded frame
        [in#0/mpegts @ 0xae1004000] [verbose]   Input stream #0:0 (video): 149 packets read (175739 bytes); 149 frames decoded; 0 decode errors;
        [in#0/mpegts @ 0xae1004000] [verbose]   Input stream #0:1 (audio): 250 packets read (288018 bytes); 249 frames decoded; 1 decode errors (286848 samples);
        """;

    private const string WreckedCluster = """
        [in#0/matroska,webm @ 0xb9b000000] [error] Element at 0x144a7e ending at 0x100144987 exceeds containing master element ending at 0x28521d
        [in#0/matroska,webm @ 0xb9b000000] [error] 0x00 at pos 1329799 (0x144a87) invalid as first byte of an EBML number
        [in#0/matroska,webm @ 0xb9ac2c000] [verbose]   Input stream #0:0 (video): 5975 packets read (430154 bytes); 5975 frames decoded; 0 decode errors;
        [in#0/matroska,webm @ 0xb9ac2c000] [verbose]   Input stream #0:1 (audio): 11205 packets read (2076176 bytes); 11204 frames decoded; 0 decode errors (11472896 samples);
        """;

    // A 25 fps clip joined to a 30 fps clip, decoded with passthrough timing: nothing to report.
    private const string MixedFrameRates = """
        [in#0/matroska,webm @ 0xb10c2c000] [verbose]   Input stream #0:0 (video): 220 packets read (35963 bytes); 220 frames decoded; 0 decode errors;
        """;

    // A seek to an open-GOP keyframe: the decoder drops the leading pictures and says so, the file is intact.
    private const string OpenGopSeek = """
        [info] Input #0, matroska,webm, from 'open-gop.mkv':
        [h264 @ 0x9e8025500] [error] mmco: unref short failure
        [in#0/matroska,webm @ 0x9e8c2c000] [verbose]   Input stream #0:0 (video): 50 packets read (40211 bytes); 48 frames decoded; 0 decode errors;
        """;

    // ffmpeg before 7: demuxer messages carry the input format's name, and the summary has no decode error count.
    private const string OlderFfmpeg = """
        [matroska,webm @ 0x7f8] [error] Element at 0x144a7e ending at 0x100144987 exceeds containing master element ending at 0x28521d
        [info] Input #0, matroska,webm, from 'wrecked.mkv':
        [verbose] Input stream #0:0 (video): 149 packets read (175739 bytes); 149 frames decoded;
        """;

    [Fact]
    public void DecodeLogsCountDemuxerAndDecoderDamageButNotTimingOrDiscardedPictures()
    {
        var glitched = DecodeLog.Parse(GlitchedTransportStream);
        Assert.Equal((0, 3, 3, 398L), (glitched.DemuxerErrors, glitched.CountedErrors, glitched.Errors, glitched.FramesDecoded));
        Assert.Equal(2, DecodeLog.Parse(WreckedCluster).Errors);
        Assert.Equal((0, 220L), (DecodeLog.Parse(MixedFrameRates).Errors, DecodeLog.Parse(MixedFrameRates).FramesDecoded));
        Assert.Equal(0, DecodeLog.Parse(OpenGopSeek).Errors);
        var older = DecodeLog.Parse(OlderFfmpeg);
        Assert.Equal((1, 149L), (older.Errors, older.FramesDecoded));
        Assert.Null(DecodeLog.Parse("").FramesDecoded);
    }

    [Fact]
    public async Task ABrokenMatroskaClusterIsReportedEvenWhenNoDecoderComplains()
    {
        var path = await CreateAsync("long.mkv", ["-c:v", "libx264", "-g", "25", "-c:a", "aac"], seconds: 240, size: "64x48");
        var bytes = await File.ReadAllBytesAsync(path);
        var cluster = bytes.AsSpan(bytes.Length / 2).IndexOf(new byte[] { 0x1F, 0x43, 0xB6, 0x75 }) + bytes.Length / 2;
        new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 }.CopyTo(bytes, cluster + 4);
        await File.WriteAllBytesAsync(path, bytes);

        var report = await new MediaValidator(_tools, new MediaProcess())
            .ValidateDecodedAsync(path, TimeSpan.FromSeconds(240), DecodeTolerance.ForImport(isMpegStream: false));

        Assert.True(report.ErrorCount > 0);
    }

    [Theory]
    [InlineData("ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers", 9, 0)]
    [InlineData("ffmpeg version n6.1.1 Copyright (c) 2000-2023", 6, 1)]
    [InlineData("mkvmerge v103.0 ('The Darkness') 64-bit", 103, 0)]
    public void ToolVersionsAreReadFromTheirBanners(string banner, int major, int minor) =>
        Assert.Equal(new Version(major, minor), MediaTools.ParseVersion(banner));

    [Fact]
    public void DevelopmentBuildsWithoutAVersionAreAccepted() =>
        Assert.Null(MediaTools.ParseVersion("ffmpeg version N-112345-g0123abcd Copyright (c) 2000-2026"));

    private async Task<string> CreateAsync(string name, IEnumerable<string> codecs, int seconds = 6, string size = "160x120")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", $"testsrc=size={size}:rate=25",
            "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), .. codecs, path],
            TimeSpan.FromSeconds(60));
        Assert.Equal(0, result.ExitCode);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
