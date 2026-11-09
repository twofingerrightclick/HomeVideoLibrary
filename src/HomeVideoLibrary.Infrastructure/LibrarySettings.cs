using System.Text.Json;

namespace HomeVideoLibrary.Infrastructure;

public sealed class LibrarySettings(string? settingsPath = null)
{
    private readonly string _settingsPath = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeVideoLibrary", "settings.json");

    /// <summary>Returns the saved library root, or null when none is saved or the file is empty or corrupt.</summary>
    public async Task<string?> LoadRootAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
            return null;
        await using var stream = File.OpenRead(_settingsPath);
        try
        {
            var settings = await JsonSerializer.DeserializeAsync<Settings>(stream, cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(settings?.LibraryRoot) ? null : settings.LibraryRoot;
        }
        catch (JsonException exception)
        {
            System.Diagnostics.Trace.TraceWarning($"Ignoring unreadable settings {_settingsPath}: {exception.Message}");
            return null;
        }
    }

    public async Task SaveRootAsync(string root, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, new Settings(Path.GetFullPath(root)), cancellationToken: cancellationToken);
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed record Settings(string LibraryRoot);
}
