namespace HomeVideoLibrary.Desktop.ViewModels;

// TODO(U2): replaced by VideoPageViewModel; delete with VideoEditorViewModel.
/// <summary>Hosts the existing video editor as a page until the new video page replaces it.</summary>
public sealed class VideoEditorPage(VideoEditorViewModel editor) : PageViewModel
{
    public VideoEditorViewModel Editor { get; } = editor;

    public override Task LoadAsync() => Editor.LoadAsync();

    public override async Task<bool> CanLeaveAsync(IInteraction interaction)
    {
        if (Editor.IsBusy) return false;
        if (!Editor.IsDirty) return true;
        switch (await interaction.AskLeaveAsync(Editor.Title.Length > 0 ? Editor.Title : Editor.TitlePlaceholder))
        {
            case LeaveChoice.KeepEditing: return false;
            case LeaveChoice.Save:
                await Editor.SaveAsync();
                return !Editor.IsDirty;
            default: return true;
        }
    }

    public override Task SettleForCloseAsync() => WhenIdleAsync(Editor, () => Editor.IsBusy);

    public override ValueTask DisposeAsync() => Editor.DisposeAsync();

    internal static async Task WhenIdleAsync(ViewModelBase viewModel, Func<bool> isBusy)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (!isBusy()) idle.TrySetResult(); }
        viewModel.PropertyChanged += Changed;
        try { if (isBusy()) await idle.Task; }
        finally { viewModel.PropertyChanged -= Changed; }
    }
}

// TODO(U3): replaced by TrimPanelViewModel and MergePageViewModel; delete with ClipEditorViewModel.
/// <summary>Hosts the existing clip editor as a page until the edit tab and merge page replace it.</summary>
public sealed class ClipEditorPage(ClipEditorViewModel editor) : PageViewModel
{
    public ClipEditorViewModel Editor { get; } = editor;

    public override Task LoadAsync() => Editor.LoadFormatAsync();

    public override Task<bool> CanLeaveAsync(IInteraction interaction) => Task.FromResult(!Editor.IsBusy);

    public override Task SettleForCloseAsync()
    {
        Editor.Cancel();
        return VideoEditorPage.WhenIdleAsync(Editor, () => Editor.IsBusy);
    }

    public override ValueTask DisposeAsync()
    {
        Editor.Dispose();
        return ValueTask.CompletedTask;
    }
}
