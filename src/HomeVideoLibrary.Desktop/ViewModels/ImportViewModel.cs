using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Importing picked files one by one, with progress, per-file results and cancellation. Each imported video
/// is published to the model as soon as it is committed, even when the import is then cancelled.</summary>
public sealed class ImportViewModel : ViewModelBase
{
    private readonly Func<LibraryModel?> _model;
    private readonly Func<LibraryCatalog, IVideoImporter> _createImporter;
    private readonly IInteraction _interaction;
    private readonly Func<bool> _canStart;
    private readonly Action<string> _status;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _finished;
    private IReadOnlyList<string> _results = [];
    private bool _isImporting, _isProgressIndeterminate;
    private double _progress;

    /// <param name="canStart">Whether the rest of the app allows an import now (a library is open and its tools work).</param>
    public ImportViewModel(Func<LibraryModel?> model, Func<LibraryCatalog, IVideoImporter> createImporter, IInteraction interaction,
        Func<bool> canStart, Action<string> status)
    {
        _model = model; _createImporter = createImporter; _interaction = interaction; _canStart = canStart; _status = status;
        ImportCommand = new AsyncCommand(_ => PickAndImportAsync(), () => CanImport, context: "Import");
        CancelCommand = new RelayCommand(Cancel, () => IsImporting);
    }

    /// <summary>Asks for files, then imports them.</summary>
    public AsyncCommand ImportCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool CanImport => _model() is not null && !IsImporting && _canStart();
    public bool IsImporting { get => _isImporting; private set { if (SetProperty(ref _isImporting, value)) UpdateCommands(); } }
    /// <summary>0..100 and never decreasing within one import. See <see cref="IsProgressIndeterminate"/>.</summary>
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => SetProperty(ref _isProgressIndeterminate, value); }
    /// <summary>One line per file of the last import, kept when it was cancelled.</summary>
    public IReadOnlyList<string> Results { get => _results; private set => SetProperty(ref _results, value); }

    public void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
    }

    private async Task PickAndImportAsync()
    {
        var picked = await _interaction.PickVideosAsync();
        await ImportAsync(picked.Paths, picked.Unavailable);
    }

    /// <param name="unavailablePicks">Picked items that were skipped because they are not local files.</param>
    public async Task ImportAsync(IReadOnlyList<string> sources, int unavailablePicks = 0)
    {
        var skipped = unavailablePicks > 0 ? $"{unavailablePicks} picked item(s) are not files on this computer and were skipped." : null;
        if (sources.Count == 0 && skipped is not null) _status(skipped);
        if (_model() is not { } model || sources.Count == 0 || IsImporting || !_canStart()) return;
        IsImporting = true; Progress = 0; IsProgressIndeterminate = false;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var finished = _finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = skipped is null ? new List<string>() : [skipped];
        int imported = 0, alreadyThere = 0, failed = 0;
        try
        {
            var importer = _createImporter(model.Catalog);
            for (var index = 0; index < sources.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(sources[index]);
                var prefix = sources.Count == 1 ? name : $"File {index + 1} of {sources.Count} · {name}";
                _status(prefix);
                var first = index;
                var progress = new Progress<ImportProgress>(update =>
                {
                    if (!ReferenceEquals(_cancellation, cancellation)) return;
                    _status($"{prefix} — {update.Stage}");
                    IsProgressIndeterminate = update.Fraction is null;
                    if (update.Fraction is { } fraction)
                        Progress = Math.Max(Progress, (first + Math.Clamp(fraction, 0, 1)) / sources.Count * 100);
                });
                try
                {
                    var source = sources[index];
                    var result = await Task.Run(() => importer.ImportAsync(source, progress, cancellation.Token));
                    results.AddRange(result.Warnings.Select(warning => $"{name}: {warning}"));
                    if (result.Outcome == ImportOutcome.AlreadyInLibrary)
                    {
                        alreadyThere++;
                        results.Add($"{name}: already in library");
                    }
                    else
                    {
                        imported++;
                        // The video is committed; publish it even if the import is being cancelled.
                        var record = await Task.Run(() => model.UpsertFromFileAsync(result.Video, CancellationToken.None));
                        results.Add(record.State == VideoRecordState.Ok ? $"{name}: imported" : $"{name}: imported; details could not be read yet ({record.Error})");
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    failed++;
                    results.Add($"{name}: {exception.Message}");
                }
                Progress = Math.Max(Progress, (index + 1.0) / sources.Count * 100);
                IsProgressIndeterminate = false;
            }
            var summary = failed == 0 ? $"Imported {imported} video(s)." : $"Imported {imported}; {failed} could not be imported.";
            if (alreadyThere > 0) summary += $" {alreadyThere} already in library.";
            _status(summary + string.Concat(results.Select(line => Environment.NewLine + line)));
        }
        catch (OperationCanceledException)
        {
            _status($"Import cancelled. {imported} imported video(s) kept; originals unchanged." + string.Concat(results.Select(line => Environment.NewLine + line)));
        }
        finally
        {
            Results = results;
            _cancellation = null; IsImporting = false; IsProgressIndeterminate = false;
            finished.TrySetResult();
        }
    }

    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Cancels a running import and waits until it has cleaned up.</summary>
    public async Task CancelAndWaitAsync()
    {
        var finished = _finished;
        Cancel();
        if (finished is not null) await finished.Task;
    }
}
