using System.Text.Json;

namespace HomeVideoLibrary.Infrastructure;

/// <summary>Per-user app settings in one JSON file. Every save rewrites the whole file atomically and keeps the other
/// settings, so saving one value never loses another.</summary>
public sealed class LibrarySettings(string? settingsPath = null)
{
    private readonly string _settingsPath = settingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeVideoLibrary", "settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Returns the saved library root, or null when none is saved or the file is empty or corrupt.</summary>
    public async Task<string?> LoadRootAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(settings.LibraryRoot) ? null : settings.LibraryRoot;
    }

    /// <summary>Whether button sounds are on; off when unset or unreadable.</summary>
    public async Task<bool> LoadSoundEffectsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).SoundEffects;

    public Task SaveRootAsync(string root, CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings with { LibraryRoot = Path.GetFullPath(root) }, cancellationToken);

    public Task SaveSoundEffectsAsync(bool enabled, CancellationToken cancellationToken = default) =>
        UpdateAsync(settings => settings with { SoundEffects = enabled }, cancellationToken);

    private async Task UpdateAsync(Func<Settings, Settings> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var settings = change(await LoadAsync(cancellationToken));
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var temporaryPath = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken);
                File.Move(temporaryPath, _settingsPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<Settings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
            return new Settings();
        await using var stream = File.OpenRead(_settingsPath);
        try
        {
            return await JsonSerializer.DeserializeAsync<Settings>(stream, cancellationToken: cancellationToken) ?? new Settings();
        }
        catch (JsonException exception)
        {
            System.Diagnostics.Trace.TraceWarning($"Ignoring unreadable settings {_settingsPath}: {exception.Message}");
            return new Settings();
        }
    }

    private sealed record Settings(string? LibraryRoot = null, bool SoundEffects = false);
}
