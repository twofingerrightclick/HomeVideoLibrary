using System.Collections.Concurrent;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.Editing;

/// <summary>Runs a page's clip edits and reports them: the stage, the progress bar, the final status and the new
/// videos the last edit saved.</summary>
/// <param name="outputsCreated">Publishes committed outputs to the library. It runs even when cancellation was
/// requested after the service committed, because the new files already exist; a failure is shown as a warning.</param>
public sealed class ClipEditRun(string status, Func<IReadOnlyList<LibraryVideo>, Task> outputsCreated) : ViewModelBase
{
    public const string CancelledStatus = "Editing cancelled; no new outputs published. Originals unchanged.";

    private string _status = status;
    private double _progress;
    private bool _isProgressIndeterminate;
    private IReadOnlyList<LibraryVideo> _outputs = [];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>0..100; never goes backwards during a run.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }

    /// <summary>The videos the last successful edit added, in output order; empty until one succeeds.</summary>
    public IReadOnlyList<LibraryVideo> Outputs
    {
        get => _outputs;
        private set
        {
            if (!SetProperty(ref _outputs, value)) return;
            OnPropertyChanged(nameof(HasOutputs));
            OnPropertyChanged(nameof(SavedLabel));
        }
    }

    public bool HasOutputs => Outputs.Count > 0;

    /// <summary>Shows a message about the page (for example why it cannot be left yet) until the next update.</summary>
    public void Notify(string message) => Status = message;

    public string SavedLabel => Outputs.Count == 1 ? "Saved 1 new video" : $"Saved {Outputs.Count} new videos";

    /// <summary>Runs <paramref name="operation"/> on the thread pool and reports it. Never throws: cancellation and
    /// failures end in <see cref="Status"/>. Call on the UI thread; progress is marshalled back to it.</summary>
    public async Task RunAsync(Func<IProgress<ClipEditProgress>, CancellationToken, Task<IReadOnlyList<LibraryVideo>>> operation,
        CancellationToken cancellationToken)
    {
        Outputs = []; Progress = 0; IsProgressIndeterminate = true;
        // Warnings are collected on the reporting thread so none can arrive after the final status is written.
        var warnings = new ConcurrentQueue<string>();
        var display = new Progress<ClipEditProgress>(update =>
        {
            Status = update.Stage;
            IsProgressIndeterminate = update.Fraction is null;
            if (update.Fraction is { } fraction) Progress = Math.Max(Progress, fraction * 100);
        });
        var progress = new RelayProgress(update =>
        {
            if (update.Warning is { } warning) warnings.Enqueue(warning);
            else ((IProgress<ClipEditProgress>)display).Report(update);
        });
        try
        {
            var outputs = await Task.Run(() => operation(progress, cancellationToken), CancellationToken.None);
            try { await Task.Run(() => outputsCreated(outputs), CancellationToken.None); }
            catch (Exception exception) { warnings.Enqueue("Search refresh needed: " + exception.Message); }
            Outputs = outputs;
            Status = string.Join(" ", [SavedLabel + " in the library. Originals unchanged.", .. warnings.Distinct()]);
            Progress = 100;
        }
        catch (OperationCanceledException) { Status = CancelledStatus; }
        catch (Exception exception) { Status = exception.Message; }
        finally { IsProgressIndeterminate = false; }
    }

    private sealed class RelayProgress(Action<ClipEditProgress> report) : IProgress<ClipEditProgress>
    {
        public void Report(ClipEditProgress value) => report(value);
    }
}
