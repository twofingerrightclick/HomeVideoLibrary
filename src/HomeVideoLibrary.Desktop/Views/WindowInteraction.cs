using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>The app's <see cref="IInteraction"/>: modal windows and platform pickers over the main window.</summary>
public sealed class WindowInteraction(Func<Window?> owner) : IInteraction
{
    private static readonly FilePickerFileType VideoFiles = new("Videos")
    {
        Patterns = VideoCandidates.Extensions.Select(extension => "*" + extension).ToArray(),
        MimeTypes = ["video/*"],
        AppleUniformTypeIdentifiers = ["public.movie"],
    };

    public Task<LeaveChoice> AskLeaveAsync(string title, string? cannotSave)
    {
        if (owner() is not { } window) return Task.FromResult(LeaveChoice.KeepEditing);
        var message = $"“{title}” has changes that are not saved in the video file yet.";
        var keys = new List<PromptWindow<LeaveChoice>.Option>
        {
            new("KEEP EDITING", LeaveChoice.KeepEditing, IsDefault: cannotSave is not null, IsCancel: true),
            new("DISCARD", LeaveChoice.Discard, "destructive"),
        };
        if (cannotSave is null) keys.Add(new("SAVE AND LEAVE", LeaveChoice.Save, "primary", IsDefault: true));
        else message += $" They cannot be saved now. {cannotSave}";
        return new PromptWindow<LeaveChoice>("UNSAVED CHANGES", message, LeaveChoice.KeepEditing, keys).AskAsync(window);
    }

    public Task ShowErrorAsync(string title, string message) => owner() is { } window
        ? new PromptWindow<bool>(title, message, true, [new("OK", true, "primary", IsDefault: true, IsCancel: true)]).AskAsync(window)
        : Task.CompletedTask;

    public Task<bool> ConfirmAsync(Confirmation confirmation) => owner() is { } window
        ? new PromptWindow<bool>(confirmation.Title, confirmation.Message, false,
            [
                new("CANCEL", false, IsDefault: true, IsCancel: true),
                new(confirmation.ConfirmLabel, true, "destructive"),
            ], confirmation.Details).AskAsync(window)
        : Task.FromResult(false);

    public async Task<PickedFiles> PickVideosAsync()
    {
        if (owner() is not { } window) return PickedFiles.None;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import videos into your library", AllowMultiple = true, FileTypeFilter = [VideoFiles, FilePickerFileTypes.All],
        });
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        return new PickedFiles(paths, files.Count - paths.Length);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (owner() is not { } window) return null;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task ShowSettingsAsync(SettingsViewModel settings) => owner() is { } window
        ? new SettingsWindow { DataContext = settings }.ShowDialog(window)
        : Task.CompletedTask;
}
