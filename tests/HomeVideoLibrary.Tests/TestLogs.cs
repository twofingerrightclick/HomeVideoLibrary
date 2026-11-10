using System.Runtime.CompilerServices;
using HomeVideoLibrary.Desktop;

namespace HomeVideoLibrary.Tests;

internal static class TestLogs
{
    /// <summary>Errors reported during tests go to a temporary log, never the user's real one.</summary>
    [ModuleInitializer]
    internal static void Redirect() => AppErrors.UseLogDirectory(Path.Combine(Path.GetTempPath(), "hvl-test-logs"));
}
