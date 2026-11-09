using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels.Editing;
using HomeVideoLibrary.Desktop.Views.Editing;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class EditingViewTests
{
    [AvaloniaFact]
    public void Reordering_a_merge_keeps_the_list_selection()
    {
        var page = Merge();
        var (window, list) = Show(page);
        var second = page.Sources[1];
        list.SelectedItem = second;
        Dispatcher.UIThread.RunJobs();

        page.MoveUpCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(second, page.Sources[0]);
        Assert.Same(second, page.SelectedSource);
        Assert.Same(second, list.SelectedItem);
        Assert.Same(second, page.SelectedSource);

        list.ContainerFromIndex(0)!.Focus();
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(second, page.Sources[1]);
        Assert.Same(second, list.SelectedItem);
    }

    [AvaloniaFact]
    public void Enter_commits_a_typed_in_point()
    {
        var panel = new TrimPanelViewModel(Video("Source", TimeSpan.FromMinutes(10)), new NoEditing(), () => TimeSpan.Zero,
            _ => Task.CompletedTask, _ => Task.CompletedTask, () => false);
        var window = new Window { Content = new EditTab { DataContext = panel }, Width = 340, Height = 900 };
        window.Show();
        var field = window.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "InField");

        field.Focus();
        window.KeyTextInput("1:30");
        Assert.Null(panel.In);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TimeSpan.FromSeconds(90), panel.In);
        Assert.Equal("0:01:30.000", field.Text);
    }

    private static (Window Window, ListBox List) Show(MergePageViewModel page)
    {
        var window = new Window { Content = new MergePageView { DataContext = page }, Width = 1000, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, window.GetVisualDescendants().OfType<ListBox>().Single());
    }

    private static MergePageViewModel Merge() =>
        new([Video("Christmas '91", TimeSpan.FromMinutes(42)), Video("Lake Tahoe", TimeSpan.FromMinutes(12)), Video("Little league", TimeSpan.FromMinutes(7))],
            new NoEditing(), _ => Task.CompletedTask, _ => Task.CompletedTask, () => Task.CompletedTask);

    private static LibraryVideo Video(string name, TimeSpan duration) =>
        new(Guid.NewGuid(), $"Videos/{name}.mkv", name, duration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    /// <summary>Plans never finish, so the views show their pending state without touching media.</summary>
    private sealed class NoEditing : IClipEditingService
    {
        public Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
            CancellationToken cancellationToken = default) => new TaskCompletionSource<ClipCutPlan>().Task;
        public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<ClipCutPlan>().Task;
        public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default) =>
            new TaskCompletionSource<ClipMergePlan>().Task;
        public Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
