using HomeVideoLibrary.Desktop.Playback;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class LibVlcDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-vlc-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARelocatedBundleUsesItsEngineUnlessExplicitlyOverridden()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var executableDirectory = Path.Combine(_root, "Renamed App.app", "Contents", "MacOS");
        var library = Path.Combine(_root, "Renamed App.app", "Contents", "Resources", "Native", "vlc", "lib");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "libvlc.dylib"), "bundled fixture");

        Assert.Equal(library, LibVlcPlayback.FindInstalledVlc(executableDirectory));
        Assert.Equal(library, LibVlcPlayback.FindInstalledVlc(executableDirectory, " "));
        Assert.Equal("/explicit/runtime", LibVlcPlayback.FindInstalledVlc(executableDirectory, "/explicit/runtime"));
        File.Delete(Path.Combine(library, "libvlc.dylib"));
        Assert.NotEqual(library, LibVlcPlayback.FindInstalledVlc(executableDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
