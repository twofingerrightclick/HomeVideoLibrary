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

    private async Task<string> CreateAsync(string name, IEnumerable<string> codecs)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        var result = await new MediaProcess().RunAsync(_tools.Ffmpeg, ["-v", "error", "-n", "-f", "lavfi", "-i", "testsrc=size=160x120:rate=25",
            "-f", "lavfi", "-i", "sine=sample_rate=48000", "-t", "6", .. codecs, path], TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
