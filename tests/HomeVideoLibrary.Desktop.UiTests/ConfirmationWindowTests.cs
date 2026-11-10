using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class ConfirmationWindowTests
{
    private static readonly Confirmation Question = new("MOVE TO TRASH", "Move 2 videos to Trash?", ["first · 1 GB · in Holidays", "second · 2 GB"],
        "MOVE TO TRASH");

    [AvaloniaTheory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Enter)]
    public void EnterAndEscapeBothKeepTheVideos(Key key)
    {
        var (answer, prompt) = Ask();

        // The dialog is not a headless input root, so the key is raised on it directly.
        prompt.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = prompt });
        Dispatcher.UIThread.RunJobs();

        Assert.True(answer.IsCompleted);
        Assert.False(answer.Result);
    }

    [AvaloniaFact]
    public void TheDialogListsTheVideosAndOnlyItsNamedKeyAgrees()
    {
        var (answer, prompt) = Ask();

        var lines = prompt.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
        Assert.Contains("Move 2 videos to Trash?", lines);
        Assert.Contains("first · 1 GB · in Holidays", lines);
        var keys = prompt.GetVisualDescendants().OfType<Button>().ToArray();
        Assert.True(Assert.Single(keys, key => key.IsDefault).IsCancel);
        Assert.Equal("CANCEL", keys.Single(key => key.IsDefault).Content);

        keys.Single(key => Equals(key.Content, "MOVE TO TRASH")).Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(answer.Result);
    }

    private static (Task<bool> Answer, Window Prompt) Ask()
    {
        var owner = new Window();
        owner.Show();
        var answer = new WindowInteraction(() => owner).ConfirmAsync(Question);
        Dispatcher.UIThread.RunJobs();
        return (answer, Assert.Single(owner.OwnedWindows));
    }
}
