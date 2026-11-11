using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class MediaToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-tools-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARelocatedBundleFindsAllItsToolsBeforeSystemInstallations()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var executableDirectory = Path.Combine(_root, "Renamed App.app", "Contents", "MacOS");
        var bin = Path.Combine(_root, "Renamed App.app", "Contents", "Resources", "Native", "bin");
        Directory.CreateDirectory(bin);
        foreach (var name in new[] { "ffmpeg", "ffprobe", "mkvmerge", "mkvextract", "mkvpropedit" })
            File.WriteAllText(Path.Combine(bin, name), "bundled fixture");

        var tools = MediaTools.Discover(executableDirectory);

        Assert.Equal(Path.Combine(bin, "ffmpeg"), tools.Ffmpeg);
        Assert.Equal(Path.Combine(bin, "ffprobe"), tools.Ffprobe);
        Assert.Equal(Path.Combine(bin, "mkvmerge"), tools.MkvMerge);
        Assert.Equal(Path.Combine(bin, "mkvextract"), tools.MkvExtract);
        Assert.Equal(Path.Combine(bin, "mkvpropedit"), tools.MkvPropEdit);
        Assert.Equal("custom-ffmpeg", (tools with { Ffmpeg = "custom-ffmpeg" }).Ffmpeg);
    }

    [Fact]
    public void ASourceBuildRetainsInstalledToolDiscovery()
    {
        var tools = MediaTools.Discover(_root);
        Assert.Equal(MediaTools.Discover(), tools);
    }

    [Fact]
    public async Task AToolInstalledAfterAFailedProbeIsFoundOnTheNextCheck()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        var fake = Path.Combine(_root, "ffmpeg");
        var real = MediaTools.Discover();
        var tools = real with { Ffmpeg = fake };

        Assert.Equal(["ffmpeg"], await tools.FindUnavailableAsync());

        await File.WriteAllTextAsync(fake, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Empty(await tools.FindUnavailableAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
