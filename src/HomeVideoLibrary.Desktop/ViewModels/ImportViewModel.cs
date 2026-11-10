using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Importing picked files. Each file is a background job in the import lane, so files import one at a time
/// while the rest of the app stays usable; each imported video is published to the model as soon as it is committed.</summary>
public sealed class ImportViewModel : ViewModelBase
{
    private readonly Func<LibraryModel?> _model;
    private readonly Func<LibraryCatalog, IVideoImporter> _createImporter;
    private readonly IInteraction _interaction;
    private readonly JobQueue _jobs;
    private readonly Func<bool> _canStart;
    private readonly Action<string> _status;

    /// <param name="canStart">Whether the rest of the app allows an import now (a library is open, its tools work and it
    /// is not being switched or closed).</param>
    public ImportViewModel(Func<LibraryModel?> model, Func<LibraryCatalog, IVideoImporter> createImporter, IInteraction interaction,
        JobQueue jobs, Func<bool> canStart, Action<string> status)
    {
        _model = model; _createImporter = createImporter; _interaction = interaction; _jobs = jobs; _canStart = canStart; _status = status;
        ImportCommand = new AsyncCommand(_ => PickAndImportAsync(), () => CanImport, context: "Import");
        _jobs.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(JobQueue.ActiveCount)) OnPropertyChanged(nameof(IsImporting)); };
    }

    /// <summary>Asks for files, then queues them.</summary>
    public AsyncCommand ImportCommand { get; }

    public bool CanImport => _model() is not null && _canStart();
    public bool IsImporting => _jobs.Jobs.Any(job => job.Lane == JobLane.Import && !job.IsFinished);

    public void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private async Task PickAndImportAsync()
    {
        var picked = await _interaction.PickVideosAsync();
        Enqueue(picked.Paths, picked.Unavailable);
    }

    /// <summary>Queues one import job per file. <paramref name="unavailablePicks"/> counts picked items that are not
    /// local files; they are skipped with a status message.</summary>
    public IReadOnlyList<JobViewModel> Enqueue(IReadOnlyList<string> sources, int unavailablePicks = 0)
    {
        if (unavailablePicks > 0) _status($"{unavailablePicks} picked item(s) are not files on this computer and were skipped.");
        if (_model() is not { } model || !CanImport) return [];
        var importer = _createImporter(model.Catalog);
        return sources.Select(source => _jobs.Enqueue($"Import {Path.GetFileName(source)}", JobLane.Import,
            (progress, token) => ImportOneAsync(model, importer, source, progress, token))).ToArray();
    }

    private static async Task<JobOutcome> ImportOneAsync(LibraryModel model, IVideoImporter importer, string source,
        IProgress<JobProgress> progress, CancellationToken cancellationToken)
    {
        var report = new Progress<ImportProgress>(update => progress.Report(new JobProgress(update.Stage, update.Fraction)));
        var result = await importer.ImportAsync(source, report, cancellationToken);
        var warnings = string.Concat(result.Warnings.Select(warning => " " + warning));
        if (result.Outcome == ImportOutcome.AlreadyInLibrary)
            return new JobOutcome("Already in the library." + warnings, [result.Video.Id]);
        // The video is committed; publish it even if the job is being cancelled.
        var record = await model.UpsertFromFileAsync(result.Video, CancellationToken.None);
        return new JobOutcome((record.State == VideoRecordState.Ok ? "Imported." : $"Imported; details could not be read yet ({record.Error}).")
            + warnings, [record.Id]);
    }
}
