using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public string Title => "Home Video Library";
    public string WelcomeMessage => "A home for the moments you want to keep.";
    private readonly LibrarySettings _settings = new();
    private LibraryCatalog? _catalog;
    private CancellationTokenSource? _importCancellation;
    private string _libraryRoot = "Choose a library folder to begin";
    private string _status = "Your original files stay untouched.";
    private bool _isBusy;
    private double _progress;

    public ObservableCollection<LibraryVideo> Videos { get; } = [];
    public string LibraryRoot { get => _libraryRoot; private set => SetProperty(ref _libraryRoot, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanImport)); } }
    public bool CanImport => _catalog is not null && !IsBusy;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public async Task RestoreLibraryAsync()
    {
        try
        {
            var root = await _settings.LoadRootAsync();
            if (root is not null)
            {
                if (!Directory.Exists(root))
                    throw new DirectoryNotFoundException("The saved library folder is unavailable. Reconnect its drive or choose another folder.");
                await OpenLibraryAsync(root);
            }
        }
        catch (Exception exception) { Status = exception.Message; }
    }

    public async Task OpenLibraryAsync(string root)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var catalog = new LibraryCatalog(new LibraryPaths(root));
            await Task.Run(() => catalog.InitializeAsync());
            var videos = await Task.Run(() => catalog.ListAsync());
            await _settings.SaveRootAsync(root);
            _catalog = catalog;
            LibraryRoot = catalog.Paths.RootDirectory;
            Videos.Clear();
            foreach (var video in videos) Videos.Add(video);
            Status = $"{Videos.Count} videos in your library";
        }
        catch (Exception exception) { Status = exception.Message; }
        finally { IsBusy = false; }
    }

    public async Task ImportAsync(IReadOnlyList<string> sources)
    {
        if (!CanImport || _catalog is null) return;
        IsBusy = true;
        Progress = 0;
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        var imported = 0;
        var errors = new List<string>();
        try
        {
            var importer = new VideoImporter(_catalog, MediaTools.Discover());
            foreach (var source in sources)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var progress = new Progress<ImportProgress>(update =>
                {
                    Status = $"{Path.GetFileName(source)} — {update.Stage}";
                    Progress = update.Fraction is { } fraction ? fraction * 100 : 0;
                });
                try
                {
                    var video = await Task.Run(() => importer.ImportAsync(source, progress, cancellation.Token));
                    Videos.Insert(0, video);
                    imported++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { errors.Add($"{Path.GetFileName(source)}: {exception.Message}"); }
            }
            Status = errors.Count == 0 ? $"Imported {imported} video(s)." : $"Imported {imported}; {errors.Count} failed. {string.Join(Environment.NewLine, errors)}";
        }
        catch (OperationCanceledException) { Status = $"Import cancelled. {imported} completed video(s) kept; originals unchanged."; }
        finally { _importCancellation = null; IsBusy = false; }
    }

    public void CancelImport() => _importCancellation?.Cancel();
}
