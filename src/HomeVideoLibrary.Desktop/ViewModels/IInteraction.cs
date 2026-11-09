namespace HomeVideoLibrary.Desktop.ViewModels;

public enum LeaveChoice { Save, Discard, KeepEditing }

/// <summary>Modal questions a view model awaits. The app shows them as real windows, so they appear above the native
/// video surface; tests answer them directly.</summary>
public interface IInteraction
{
    /// <summary>Asks what to do with unsaved changes to <paramref name="title"/> before leaving it.</summary>
    Task<LeaveChoice> AskLeaveAsync(string title);

    Task ShowErrorAsync(string title, string message);
}
