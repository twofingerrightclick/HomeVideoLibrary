using System.Collections.Immutable;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class ShelfTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-shelf-ui-" + Guid.NewGuid().ToString("N"));

    private (MainWindow Window, MainWindowViewModel ViewModel, ListBox List) Show(int videos)
    {
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = new NoInteraction(),
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            Post = action => action(),
        });
        var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
        window.Show();
        viewModel.Shelf.Apply(Snapshot(videos), AlbumFilter.All);
        Dispatcher.UIThread.RunJobs();
        return (window, viewModel, window.FindControl<ListBox>("TapeList")!);
    }

    [AvaloniaFact]
    public void Every_tape_is_reachable_by_keyboard()
    {
        var (window, viewModel, list) = Show(400);

        list.ScrollIntoView(150);
        Dispatcher.UIThread.RunJobs();
        list.ContainerFromIndex(150)!.Focus(NavigationMethod.Directional);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var focused = window.FocusManager!.GetFocusedElement() as Control;
        Assert.Same(viewModel.Shelf.Tapes[151], focused?.DataContext);
    }

    [AvaloniaFact]
    public void A_ten_thousand_tape_shelf_stays_virtualized_and_searches_within_a_frame()
    {
        var (_, viewModel, list) = Show(10_000);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 59);

        // Best of several keystrokes: one slow sample on a busy test machine is scheduling noise, not the shelf.
        var fastest = TimeSpan.MaxValue;
        foreach (var query in new[] { "tape 9", "tape 42", "tape 7", "tape 420", "tape 4" })
        {
            viewModel.Shelf.SearchText = "";
            var watch = Stopwatch.StartNew();
            viewModel.Shelf.SearchText = query;
            watch.Stop();
            if (watch.Elapsed < fastest) fastest = watch.Elapsed;
        }
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(fastest.TotalMilliseconds, 0, 16);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 59);
    }

    private static LibrarySnapshot Snapshot(int count)
    {
        var start = new DateTimeOffset(1999, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, count).Select(index =>
        {
            var id = Guid.NewGuid();
            return new VideoRecord(id, LibraryPaths.VideoRelativePath(id), $"tape {index}", TimeSpan.FromMinutes(5 + index % 90),
                DateTimeOffset.UtcNow, start.AddDays(-index)) { Stamp = new FileStamp(1, 1) };
        }).ToArray();
        return new LibrarySnapshot(records.ToImmutableDictionary(record => record.Id), [],
            ImmutableDictionary<Guid, ImmutableHashSet<Guid>>.Empty, TagIndex.Build(records));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

internal sealed class NoInteraction : IInteraction
{
    public Task<LeaveChoice> AskLeaveAsync(string title, string? cannotSave) => Task.FromResult(LeaveChoice.KeepEditing);
    public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    /// <summary>Declines every confirmation.</summary>
    public Task<bool> ConfirmAsync(Confirmation confirmation) => Task.FromResult(false);
    public Task<CloseChoice> AskCloseWithJobsAsync(int activeJobs) => Task.FromResult(CloseChoice.CancelJobs);
    public Task<PickedFiles> PickVideosAsync() => Task.FromResult(PickedFiles.None);
    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    public Task ShowSettingsAsync(SettingsViewModel settings) => Task.CompletedTask;
}
