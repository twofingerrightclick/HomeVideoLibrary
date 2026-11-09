using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class MediaToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-tools-" + Guid.NewGuid().ToString("N"));

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
