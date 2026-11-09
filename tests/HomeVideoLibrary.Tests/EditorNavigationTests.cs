using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using Xunit;

namespace HomeVideoLibrary.Tests;

public sealed class EditorNavigationTests
{
    [Fact]
    public void ReturningToLibrarySupersedesAnEarlierWindowCloseWarning()
    {
        var navigation = new EditorExitState();
        navigation.Request(EditorExitDestination.Window); // Dirty editor defers closing the window.
        navigation.Request(EditorExitDestination.Library); // Inspector Save, then Back to library.
        Assert.Equal(EditorExitDestination.Library, navigation.Complete());
        navigation.Request(EditorExitDestination.Library); // Open and edit a different video, then Back.
        Assert.Equal(EditorExitDestination.Library, navigation.Complete()); // Discard/Save cannot close app.
    }

    [Fact]
    public void CompletedOrCancelledWindowCloseCannotLeakToAnotherEditor()
    {
        var navigation = new EditorExitState();
        navigation.Request(EditorExitDestination.Window);
        navigation.Cancel(); // Keep editing.
        Assert.Equal(EditorExitDestination.Library, navigation.Complete());
        navigation.Request(EditorExitDestination.Window);
        Assert.Equal(EditorExitDestination.Window, navigation.Complete()); // Explicit close confirmation.
        Assert.Equal(EditorExitDestination.Library, navigation.Complete()); // A later editor has no close intent.
    }

    [Fact]
    public void VisibleOpenActionRequiresASelectionAndClearsWithIt()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.CanOpenSelected);
        viewModel.SelectedVideo = new LibraryVideo(Guid.NewGuid(), "Videos/test.mkv", "Test", TimeSpan.FromSeconds(10), DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.True(viewModel.CanOpenSelected);
        Assert.False(viewModel.HasEditor); // Selecting remains available for upcoming multi-selection.
        viewModel.SelectedVideo = null;
        Assert.False(viewModel.CanOpenSelected);
    }
}
