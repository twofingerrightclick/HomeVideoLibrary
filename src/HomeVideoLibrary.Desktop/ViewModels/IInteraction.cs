namespace HomeVideoLibrary.Desktop.ViewModels;

public enum LeaveChoice { Save, Discard, KeepEditing }

/// <summary>What to do with background jobs (imports, renders) when the window closes.</summary>
public enum CloseChoice { WaitForJobs, CancelJobs, KeepOpen }

/// <summary>Files chosen in a picker: local paths, and how many picked items had no local path.</summary>
public sealed record PickedFiles(IReadOnlyList<string> Paths, int Unavailable)
{
    public static PickedFiles None { get; } = new([], 0);
}

/// <summary>A yes-or-no question before something destructive. Declining is the default answer: Enter, Esc and closing
/// the window all decline.</summary>
/// <param name="Details">One line per affected item, listed under the message.</param>
/// <param name="ConfirmLabel">The key that agrees, naming the action ("MOVE TO TRASH").</param>
public sealed record Confirmation(string Title, string Message, IReadOnlyList<string> Details, string ConfirmLabel);

/// <summary>Modal UI a view model awaits: questions, pickers and the settings window. The app shows them as real
/// windows, so they appear above the native video surface; tests answer them directly.</summary>
public interface IInteraction
{
    /// <summary>Asks what to do with unsaved changes to <paramref name="title"/> before leaving it.</summary>
    Task<LeaveChoice> AskLeaveAsync(string title);

    Task ShowErrorAsync(string title, string message);

    /// <summary>Asks <paramref name="confirmation"/>; true only when the user chose its confirm key.</summary>
    Task<bool> ConfirmAsync(Confirmation confirmation);

    /// <summary>Asks whether to wait for <paramref name="activeJobs"/> running or queued jobs, cancel them, or keep the app open.</summary>
    Task<CloseChoice> AskCloseWithJobsAsync(int activeJobs);

    /// <summary>Asks for video files to import; <see cref="PickedFiles.None"/> when cancelled.</summary>
    Task<PickedFiles> PickVideosAsync();

    /// <summary>Asks for a folder; null when cancelled or not a local folder.</summary>
    Task<string?> PickFolderAsync(string title);

    /// <summary>Shows the settings window until the user closes it.</summary>
    Task ShowSettingsAsync(SettingsViewModel settings);
}
