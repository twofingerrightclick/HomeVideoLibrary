using HomeVideoLibrary.Infrastructure;
using Xunit;
using static HomeVideoLibrary.Tests.TestLibrary;

namespace HomeVideoLibrary.Tests;

/// <summary>Every file these tests trash is one they just created in a temporary directory.</summary>
public sealed class FileTrashTests : IDisposable
{
    private const uint Uid = 4242;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-trash-" + Guid.NewGuid().ToString("N"));

    /// <summary>Uses a disk image mounted inside the test directory, so the file lands in that volume's own
    /// <c>.Trashes</c> and the user's Trash is never touched; the image is detached afterwards.</summary>
    [Fact]
    public async Task TheMacTrashMovesAFileIntoItsVolumesOwnTrash()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var image = Path.Combine(_root, "volume.dmg");
        var mount = Directory.CreateDirectory(Path.Combine(_root, "volume")).FullName;
        await HdiutilAsync("create", "-size", "8m", "-fs", "APFS", "-volname", "HVLTrashTest", image);
        await HdiutilAsync("attach", "-nobrowse", "-noautoopen", "-mountpoint", mount, image);
        try
        {
            var file = CreateFile(Path.Combine(mount, "Videos", "tape.mkv"));

            Assert.Equal(TrashResult.Trashed, new MacFileTrash().MoveToTrash(file, out var location));

            Assert.False(File.Exists(file));
            Assert.NotNull(location);
            // The location is the real path (/private/var/... for /var/...).
            Assert.Contains($"/{Path.GetFileName(_root)}/volume/.Trashes/", location);
            Assert.True(File.Exists(location));
        }
        finally { await HdiutilAsync("detach", "-force", mount); }
    }

    private static async Task HdiutilAsync(params string[] arguments)
    {
        var result = await new MediaProcess().RunAsync("/usr/bin/hdiutil", arguments, TimeSpan.FromMinutes(1));
        Assert.True(result.ExitCode == 0, $"hdiutil {arguments[0]}: {result.StandardError}{result.StandardOutput}");
    }

    [Fact]
    public void AFileThatIsNotThereIsReportedNotTrashed()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<FileNotFoundException>(() => FileTrash.ForCurrentPlatform().MoveToTrash(Path.Combine(_root, "absent.mkv")));
    }

    [Fact]
    public void TheFreedesktopHomeTrashRecordsWhereTheFileCameFromAndNumbersTakenNames()
    {
        if (OperatingSystem.IsWindows()) return;
        var home = Path.Combine(_root, "home", "Trash");
        var trash = new FreedesktopTrash(home, _ => "/", Uid);
        var first = CreateFile(Path.Combine(_root, "Videos", "tape 1.mkv"));

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(first));
        var second = CreateFile(first);
        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(second));

        Assert.False(File.Exists(first));
        Assert.Equal(["tape 1.2.mkv", "tape 1.mkv"], Names(Path.Combine(home, "files")));
        Assert.Equal(["tape 1.2.mkv.trashinfo", "tape 1.mkv.trashinfo"], Names(Path.Combine(home, "info")));
        var info = File.ReadAllLines(Path.Combine(home, "info", "tape 1.mkv.trashinfo"));
        Assert.Equal("[Trash Info]", info[0]);
        Assert.Equal("Path=" + Path.GetFullPath(first).Replace(" ", "%20"), info[1]);
        Assert.Matches(@"^DeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d$", info[2]);
    }

    [Fact]
    public void AFileOnAnotherDeviceGoesToThatDevicesOwnTrashWithARelativePath()
    {
        if (OperatingSystem.IsWindows()) return;
        var device = Path.Combine(_root, "external");
        var home = Path.Combine(_root, "home", "Trash");
        var trash = new FreedesktopTrash(home, path => Path.GetFullPath(path).StartsWith(device, StringComparison.Ordinal) ? device : "/", Uid);
        var file = CreateFile(Path.Combine(device, "Library", "Videos", "a%b.mkv"));

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(file));

        Assert.False(Directory.Exists(home));
        var own = Path.Combine(device, $".Trash-{Uid}");
        Assert.True(File.Exists(Path.Combine(own, "files", "a%b.mkv")));
        Assert.Contains("Path=Library/Videos/a%25b.mkv", File.ReadAllLines(Path.Combine(own, "info", "a%b.mkv.trashinfo")));
    }

    [Fact]
    public void AnAdministratorsStickyTrashIsUsedAndASymlinkedOneIsNot()
    {
        if (OperatingSystem.IsWindows()) return;
        var sticky = Path.Combine(_root, "sticky");
        var shared = Directory.CreateDirectory(Path.Combine(sticky, ".Trash"));
        File.SetUnixFileMode(shared.FullName, (UnixFileMode)0b1_111_111_111);
        var linked = Path.Combine(_root, "linked");
        Directory.CreateDirectory(linked);
        Directory.CreateSymbolicLink(Path.Combine(linked, ".Trash"), shared.FullName);
        var trash = new FreedesktopTrash(Path.Combine(_root, "home", "Trash"),
            path => Path.GetFullPath(path).StartsWith(sticky, StringComparison.Ordinal) ? sticky
                : Path.GetFullPath(path).StartsWith(linked, StringComparison.Ordinal) ? linked : "/", Uid);

        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(CreateFile(Path.Combine(sticky, "one.mkv"))));
        Assert.Equal(TrashResult.Trashed, trash.MoveToTrash(CreateFile(Path.Combine(linked, "two.mkv"))));

        Assert.True(File.Exists(Path.Combine(shared.FullName, Uid.ToString(), "files", "one.mkv")));
        Assert.True(File.Exists(Path.Combine(linked, $".Trash-{Uid}", "files", "two.mkv")));
    }

    [Fact]
    public void AFileWhoseDeviceIsUnknownStaysWhereItIs()
    {
        if (OperatingSystem.IsWindows()) return;
        var file = CreateFile(Path.Combine(_root, "Videos", "kept.mkv"));
        var trash = new FreedesktopTrash(Path.Combine(_root, "home", "Trash"), path => path.EndsWith("Videos", StringComparison.Ordinal) ? null : "/", Uid);

        Assert.Equal(TrashResult.Unavailable, trash.MoveToTrash(file));
        Assert.True(File.Exists(file));
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test video");
        return path;
    }

    private static string[] Names(string directory) =>
        Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    public void Dispose() => DeleteDirectory(_root);
}
