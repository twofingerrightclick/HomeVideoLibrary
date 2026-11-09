using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class PageHostTests
{
    [AvaloniaFact]
    public void Each_page_gets_a_new_view()
    {
        var host = new ContentControl { ContentTemplate = PageViews.Template };
        var window = new Window { Content = host };
        window.Show();

        host.Content = Page("A");
        Dispatcher.UIThread.RunJobs();
        var first = host.GetVisualDescendants().OfType<UserControl>().First();
        host.Content = Page("B");
        Dispatcher.UIThread.RunJobs();
        var second = host.GetVisualDescendants().OfType<UserControl>().First();

        Assert.NotSame(first, second);
    }

    private static ClipEditorPage Page(string name) => new(new ClipEditorViewModel(
        [new LibraryVideo(Guid.NewGuid(), "Videos/a.mkv", name, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)],
        new NoClipEditing()));

    private sealed class NoClipEditing : IClipEditingService
    {
        public Task<ClipOutputFormat> GetSourceFormatAsync(LibraryVideo source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipCutPlan> PlanTrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, ClipCutMode mode = ClipCutMode.Precise,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipCutPlan> PlanSplitAsync(LibraryVideo source, TimeSpan position, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClipMergePlan> PlanMergeAsync(IReadOnlyList<LibraryVideo> sources, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryVideo>> TrimAsync(LibraryVideo source, TimeSpan start, TimeSpan end, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default, ClipCutMode mode = ClipCutMode.Precise) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryVideo>> SplitAsync(LibraryVideo source, TimeSpan position, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LibraryVideo>> MergeAsync(IReadOnlyList<LibraryVideo> sources, string title,
            IProgress<ClipEditProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
