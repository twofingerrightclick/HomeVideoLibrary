using HomeVideoLibrary.Desktop;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class AppErrorsTests
{
    [Fact]
    public void ReportedErrorsInTestsAreLoggedOutsideTheUsersLogDirectory()
    {
        var userLogs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeVideoLibrary", "logs");
        Assert.NotEqual(Path.GetFullPath(userLogs), AppErrors.LogDirectory);

        AppErrors.Report(new InvalidOperationException("probe " + Guid.NewGuid()), "Test");

        Assert.NotEmpty(Directory.EnumerateFiles(AppErrors.LogDirectory, "app-*.log"));
    }
}
