namespace HomeVideoLibrary.Desktop.ViewModels;

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
        return WhenIdleAsync(Editor, () => Editor.IsBusy);
    }

    public override ValueTask DisposeAsync()
    {
        Editor.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task WhenIdleAsync(ViewModelBase viewModel, Func<bool> isBusy)
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (!isBusy()) idle.TrySetResult(); }
        viewModel.PropertyChanged += Changed;
        try { if (isBusy()) await idle.Task; }
        finally { viewModel.PropertyChanged -= Changed; }
    }
}
