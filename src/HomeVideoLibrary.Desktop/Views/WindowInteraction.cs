using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

/// <summary>The app's <see cref="IInteraction"/>: modal windows and platform pickers over the main window.</summary>
public sealed class WindowInteraction(Func<Window?> owner) : IInteraction
{
    private static readonly FilePickerFileType VideoFiles = new("Videos")
    {
        Patterns = ["*.mp4", "*.m4v", "*.mov", "*.mkv", "*.avi", "*.mts", "*.m2ts", "*.ts", "*.mpg", "*.mpeg", "*.vob", "*.wmv", "*.3gp", "*.webm", "*.dv", "*.flv"],
        MimeTypes = ["video/*"],
        AppleUniformTypeIdentifiers = ["public.movie"],
    };

    public Task<LeaveChoice> AskLeaveAsync(string title)
    {
        if (owner() is not { } window) return Task.FromResult(LeaveChoice.KeepEditing);
        return new PromptWindow<LeaveChoice>("UNSAVED CHANGES",
            $"“{title}” has changes that are not saved in the video file yet.", LeaveChoice.KeepEditing,
            [
                new("KEEP EDITING", LeaveChoice.KeepEditing, IsCancel: true),
                new("DISCARD", LeaveChoice.Discard, "destructive"),
                new("SAVE AND LEAVE", LeaveChoice.Save, "primary", IsDefault: true),
            ]).AskAsync(window);
    }

    public Task<CloseChoice> AskCloseWithJobsAsync(int activeJobs)
    {
        if (owner() is not { } window) return Task.FromResult(CloseChoice.KeepOpen);
        var jobs = activeJobs == 1 ? "1 import or edit is" : $"{activeJobs} imports or edits are";
        return new PromptWindow<CloseChoice>("STILL WORKING",
            $"{jobs} still running or queued. Finished videos are already safe in the library.", CloseChoice.KeepOpen,
            [
                new("KEEP OPEN", CloseChoice.KeepOpen, IsCancel: true),
                new("CANCEL AND QUIT", CloseChoice.CancelJobs, "destructive"),
                new("QUIT WHEN DONE", CloseChoice.WaitForJobs, "primary", IsDefault: true),
            ]).AskAsync(window);
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
