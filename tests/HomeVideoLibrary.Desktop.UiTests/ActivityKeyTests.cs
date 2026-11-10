using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using HomeVideoLibrary.Desktop.ViewModels;
using HomeVideoLibrary.Desktop.Views;
using HomeVideoLibrary.Infrastructure;
using Xunit;

namespace HomeVideoLibrary.Desktop.UiTests;

public sealed class ActivityKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hvl-activity-key-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact]
    public async Task A_long_summary_trims_on_one_line_inside_the_capped_key_and_stays_whole_in_its_tooltip_and_name()
    {
        var viewModel = new MainWindowViewModel(new MainWindowDependencies
        {
            Interaction = new NoInteraction(),
            Settings = new LibrarySettings(Path.Combine(_root, "settings.json")),
            Post = action => action(),
        });
        var window = new MainWindow { DataContext = viewModel, Width = 1440, Height = 900 };
        window.Show();
        var finish = new TaskCompletionSource<JobOutcome>();
        viewModel.Jobs.Enqueue("Trim Road trip to Grandma's house for Thanksgiving", JobLane.Render, (_, _) => finish.Task);
        Dispatcher.UIThread.RunJobs();

        var key = window.FindControl<Button>("ActivityButton")!;
        var summary = window.FindControl<TextBlock>("ActivitySummary")!;
        var full = viewModel.Jobs.FullSummary;

        Assert.Equal((double)window.FindResource("ActivityKeyMaxWidth")!, key.MaxWidth);
        Assert.InRange(key.Bounds.Width, 1, key.MaxWidth);
        Assert.Single(summary.TextLayout.TextLines);
        Assert.True(summary.TextLayout.TextLines[0].HasCollapsed);
        Assert.Equal(full, ToolTip.GetTip(key));
        Assert.Equal($"Activity: {full}", AutomationProperties.GetName(key));

        finish.SetResult(JobOutcome.Done("ok"));
        await viewModel.Jobs.WhenIdleAsync();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
