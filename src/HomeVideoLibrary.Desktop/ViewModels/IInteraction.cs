namespace HomeVideoLibrary.Desktop.ViewModels;

public enum LeaveChoice { Save, Discard, KeepEditing }

/// <summary>Files chosen in a picker: local paths, and how many picked items had no local path.</summary>
public sealed record PickedFiles(IReadOnlyList<string> Paths, int Unavailable)
{
    public static PickedFiles None { get; } = new([], 0);
}

/// <summary>Modal UI a view model awaits: questions, pickers and the settings window. The app shows them as real
/// windows, so they appear above the native video surface; tests answer them directly.</summary>
public interface IInteraction
{
    /// <summary>Asks what to do with unsaved changes to <paramref name="title"/> before leaving it.</summary>
    Task<LeaveChoice> AskLeaveAsync(string title);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Asks for video files to import; <see cref="PickedFiles.None"/> when cancelled.</summary>
    Task<PickedFiles> PickVideosAsync();

    /// <summary>Asks for a folder; null when cancelled or not a local folder.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Shows the settings window until the user closes it.</summary>
    Task ShowSettingsAsync(SettingsViewModel settings);
}
