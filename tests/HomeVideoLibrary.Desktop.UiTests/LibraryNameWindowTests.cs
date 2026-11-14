using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.UI.Controls;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class LibraryNameWindowTests
{
    [AvaloniaFact]
    public async Task SaveTrimsTheNameAndClosesOnlyAfterPersistenceCompletes()
    {
        var owner = new Window();
        owner.Show();
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? result = null;
        var dialog = new LibraryNameWindow("Old library", async name => { result = name; await persisted.Task; });
        var closed = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        var field = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        var save = Assert.IsType<AsyncCommand>(dialog.GetVisualDescendants().OfType<PlasticButton>().Single(b => Equals(b.Content, "SAVE")).Command);
        field.Text = "   ";
        Assert.False(save.CanExecute(null));
        field.Text = "  Family archive  ";
        var saving = save.ExecuteAsync();
        Assert.Equal("Family archive", result);
        Assert.False(closed.IsCompleted);
        Assert.False(field.IsEnabled);
        persisted.SetResult();
        await saving;
        await closed;
        owner.Close();
    }

    [AvaloniaFact]
    public void CancelDoesNotSaveTheDraft()
    {
        var owner = new Window();
        owner.Show();
        var calls = 0;
        var dialog = new LibraryNameWindow("Old library", _ => { calls++; return Task.CompletedTask; });
        dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>()).Text = "Discard me";
        dialog.GetVisualDescendants().OfType<PlasticButton>().Single(b => Equals(b.Content, "CANCEL")).Command!.Execute(null);
        Assert.Equal(0, calls);
        Assert.False(dialog.IsVisible);
        owner.Close();
    }
}
