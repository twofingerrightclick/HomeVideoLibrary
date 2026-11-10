using System.Diagnostics;

namespace HomeVideoLibrary.Desktop;

/// <summary>The platform's file manager.</summary>
public static class FileManager
{
    /// <summary>Opens the folder holding <paramref name="path"/>, with the file selected where the platform supports it.</summary>
    public static void Reveal(string path)
    {
        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { "-R", path } }
            : OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } }
            : new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path) ?? path } };
        start.UseShellExecute = false;
        using var process = Process.Start(start);
    }
}
