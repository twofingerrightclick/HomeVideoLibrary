using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class LibrarySettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-settings-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("")]
    [InlineData("{\"LibraryRoot\":")]
    [InlineData("not json")]
    [InlineData("{\"LibraryRoot\":\"\"}")]
    public async Task EmptyOrCorruptSettingsMeanNoSavedLibrary(string content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(path, content);
        Assert.Null(await new LibrarySettings(path).LoadRootAsync());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
