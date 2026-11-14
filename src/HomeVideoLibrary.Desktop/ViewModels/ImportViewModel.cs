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
    private readonly FolderVideoDiscovery _discovery;
    private bool _folderScanDismissed;
    private string _folderScanSummary = "";
    private string _folderScanDetails = "";

    /// <param name="canStart">Whether the rest of the app allows an import now (a library is open, its tools work and it
    /// is not being switched or closed).</param>
    public ImportViewModel(Func<LibraryModel?> model, Func<LibraryCatalog, IVideoImporter> createImporter, IInteraction interaction,
        JobQueue jobs, Func<bool> canStart, Action<string> status, FolderVideoDiscovery? discovery = null)
    {
        _model = model; _createImporter = createImporter; _interaction = interaction; _jobs = jobs; _canStart = canStart; _status = status;
        _discovery = discovery ?? new FolderVideoDiscovery();
        ImportCommand = new AsyncCommand(_ => PickAndImportAsync(), () => CanImport, context: "Import");
        ImportFolderCommand = new AsyncCommand(_ => PickFolderAndImportAsync(), () => CanImport, context: "Import folder");
        DismissFolderScanCommand = new RelayCommand(() =>
        {
            _folderScanDismissed = true;
            OnPropertyChanged(nameof(HasFolderScan));
        });
        _jobs.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(JobQueue.ActiveCount)) OnPropertyChanged(nameof(IsImporting)); };
    }

    /// <summary>Asks for files, then queues them.</summary>
    public AsyncCommand ImportCommand { get; }
    public AsyncCommand ImportFolderCommand { get; }
    public RelayCommand DismissFolderScanCommand { get; }

    public string FolderScanSummary { get => _folderScanSummary; private set { SetProperty(ref _folderScanSummary, value); OnPropertyChanged(nameof(HasFolderScan)); } }
    public string FolderScanDetails { get => _folderScanDetails; private set { SetProperty(ref _folderScanDetails, value); OnPropertyChanged(nameof(HasFolderScanDetails)); } }
    public bool HasFolderScan => !_folderScanDismissed && FolderScanSummary.Length > 0;
    public bool HasFolderScanDetails => FolderScanDetails.Length > 0;

    public bool CanImport => _model() is not null && _canStart();
    public bool IsImporting => _jobs.Jobs.Any(job => job.Lane == JobLane.Import && !job.IsFinished);

    public void UpdateCommands()
    {
        OnPropertyChanged(nameof(CanImport));
        ImportCommand.RaiseCanExecuteChanged();
        ImportFolderCommand.RaiseCanExecuteChanged();
    }

    private async Task PickAndImportAsync()
    {
        var model = _model();
        var picked = await _interaction.PickVideosAsync();
        if (ReferenceEquals(model, _model())) Enqueue(picked.Paths, picked.Unavailable);
    }

    private async Task PickFolderAndImportAsync()
    {
        var model = _model();
        var folder = await _interaction.PickFolderAsync("Import videos from this folder and its subfolders");
        if (folder is not null && ReferenceEquals(model, _model())) EnqueueFolder(folder);
    }

    /// <summary>Discovery participates in the normal job lifecycle. On completion, each video gets its own ordinary
    /// import job. Cancelling discovery queues nothing; once queued, videos retain their individual cancel controls.</summary>
    public JobViewModel? EnqueueFolder(string folder)
    {
        if (_model() is not { } model || !CanImport) return null;
        _folderScanDismissed = false;
        FolderScanSummary = $"Scanning {folder} and its subfolders… See Activity to cancel.";
        FolderScanDetails = "";
        FolderScanResult? result = null;
        JobViewModel? scan = null;
        _jobs.Finished += Finished;
        scan = _jobs.Enqueue($"Scan {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}", JobLane.Import, async (progress, token) =>
        {
            result = await _discovery.ScanAsync(folder, model.Catalog.Paths.RootDirectory, new ScanProgressAdapter(progress), token);
            token.ThrowIfCancellationRequested();
            return JobOutcome.Done($"Found {result.Candidates.Count} video candidate(s).");
        }, background: true);
        return scan;

        void Finished(JobViewModel job)
        {
            if (!ReferenceEquals(job, scan)) return;
            _jobs.Finished -= Finished;
            if (result is null)
            {
                FolderScanSummary = job.State == JobState.Cancelled ? $"Folder scan cancelled: {folder}" : $"Folder scan failed: {folder}";
                FolderScanDetails = job.Error ?? "";
                return;
            }
            // This is already accepted work, including while a refused-close notice is open.
            // Switching libraries is blocked while the scan is active; still guard the captured model at handoff.
            var canQueue = job.State == JobState.Succeeded && ReferenceEquals(model, _model());
            FolderScanSummary = $"{folder}: {result.Candidates.Count} video candidate(s), {result.Skipped} skipped item(s), {result.Failed} inaccessible item(s). " +
                (canQueue ? "Each video has its own progress and result in Activity." : "Scan stopped; no videos from this scan were queued.");
            FolderScanDetails = string.Join(Environment.NewLine, result.Issues) +
                (result.OmittedIssues > 0 ? $"{Environment.NewLine}… {result.OmittedIssues} more issue(s)." : "");
            _status(FolderScanSummary);
            // Finished is synchronous on the UI thread, before the lane drains or close/switch can resume.
            if (canQueue) EnqueueFiles(model, result.Candidates, model.Catalog.Paths.RootDirectory);
        }
    }

    /// <summary>Queues one import job per file. <paramref name="unavailablePicks"/> counts picked items that are not
    /// local files; they are skipped with a status message.</summary>
    public IReadOnlyList<JobViewModel> Enqueue(IReadOnlyList<string> sources, int unavailablePicks = 0)
    {
        if (unavailablePicks > 0) _status($"{unavailablePicks} picked item(s) are not files on this computer and were skipped.");
        if (_model() is not { } model || !CanImport) return [];
        return EnqueueFiles(model, sources);
    }

    private IReadOnlyList<JobViewModel> EnqueueFiles(LibraryModel model, IReadOnlyList<string> sources, string? folderLibraryRoot = null)
    {
        var importer = _createImporter(model.Catalog);
        return sources.Select(source => _jobs.Enqueue($"Import {Path.GetFileName(source)}", JobLane.Import,
            (progress, token) => ImportOneAsync(model, importer, source, progress, token, folderLibraryRoot))).ToArray();
    }

    private async Task<JobOutcome> ImportOneAsync(LibraryModel model, IVideoImporter importer, string source,
        IProgress<JobProgress> progress, CancellationToken cancellationToken, string? folderLibraryRoot = null, DeletionHistory? allowedDeletion = null)
    {
        if (folderLibraryRoot is not null && !FolderVideoDiscovery.CanImport(source, folderLibraryRoot))
            return JobOutcome.Done("Skipped: source is now a link or inside the active library.") with { IsSkipped = true };
        var result = await importer.ImportAsync(source, new JobProgressAdapter(progress), cancellationToken, allowedDeletion);
        if (result.History is { } history)
        {
            var description = history.Kind == DeletionKind.Trashed ? "Previously trashed" : "Previously permanently deleted";
            return JobOutcome.Done($"{description} · Removed {history.RemovedAt.ToLocalTime():d}") with
            {
                IsSkipped = true,
                ImportAnyway = () =>
                {
                    if (!ReferenceEquals(model, _model()) || !CanImport)
                    {
                        _status("Import was not queued: its original library must be open and ready. Select the source again to retry.");
                        return;
                    }
                    _jobs.Enqueue($"Import {Path.GetFileName(source)}", JobLane.Import,
                        (nextProgress, token) => ImportOneAsync(model, _createImporter(model.Catalog), source,
                            nextProgress, token, folderLibraryRoot, history));
                },
            };
        }
        var video = result.Video ?? throw new InvalidDataException("The import did not return a video.");
        var warnings = string.Concat(result.Warnings.Select(warning => " " + warning));
        if (result.Outcome == ImportOutcome.AlreadyInLibrary)
            return new JobOutcome("Already in the library." + warnings, [video.Id]) { IsSkipped = true };
        // The video is committed; publish it even if the job is being cancelled.
        var record = await model.UpsertFromFileAsync(video, CancellationToken.None);
        return new JobOutcome((record.State == VideoRecordState.Ok ? "Imported." : $"Imported; details could not be read yet ({record.Error}).")
            + warnings, [record.Id]);
    }

    /// <summary>Forwards each import report straight to the job's sink, which marshals it once. A
    /// <see cref="Progress{T}"/> made here, on the pool, would have no context to keep the reports in order.</summary>
    private sealed class JobProgressAdapter(IProgress<JobProgress> job) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => job.Report(new JobProgress(value.Stage, value.Fraction));
    }

    private sealed class ScanProgressAdapter(IProgress<JobProgress> job) : IProgress<FolderScanProgress>
    {
        public void Report(FolderScanProgress value) => job.Report(new JobProgress(
            $"Scanning subfolders · {value.Candidates} video candidate(s) · {value.Skipped} skipped · {value.Failed} inaccessible"));
    }
}
