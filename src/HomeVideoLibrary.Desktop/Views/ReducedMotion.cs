using System.Diagnostics;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>Whether the user asked the OS for reduced motion. Only macOS is read (its accessibility preference);
/// elsewhere motion stays on.</summary>
public static class ReducedMotion
{
    public static async Task<bool> IsRequestedAsync()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/defaults")
            {
                ArgumentList = { "read", "com.apple.universalaccess", "reduceMotion" },
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (process is null) return false;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && output.Trim() == "1";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceWarning($"Could not read the reduced-motion preference: {exception.Message}");
            return false;
        }
    }
}
