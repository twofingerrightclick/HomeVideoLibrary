using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.Desktop.Playback;

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
    private VideoEditorViewModel? _editor;
    private bool _closeWarning;
    private bool _editorClosing;
    private readonly EditorExitState _exitState = new();
    private LibraryVideo? _selectedVideo;

    public ObservableCollection<LibraryVideo> Videos { get; } = [];
    public string LibraryRoot { get => _libraryRoot; private set => SetProperty(ref _libraryRoot, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanOpenSelected)); } }
    public bool CanImport => _catalog is not null && !IsBusy;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public VideoEditorViewModel? Editor { get => _editor; private set { SetProperty(ref _editor, value); OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(CanOpenSelected)); } }
    public LibraryVideo? SelectedVideo { get => _selectedVideo; set { SetProperty(ref _selectedVideo, value); OnPropertyChanged(nameof(CanOpenSelected)); } }
    public bool CanOpenSelected => SelectedVideo is not null && !IsBusy && !HasEditor;
    public bool HasEditor => Editor is not null;
    public bool CloseWarning { get => _closeWarning; private set => SetProperty(ref _closeWarning, value); }

    public async Task OpenVideoAsync(LibraryVideo video)
    {
        if (_catalog is null || IsBusy || Editor is not null) return;
        Editor = new VideoEditorViewModel(video, _catalog.GetVideoPath(video.RelativePath),
            new MatroskaMetadataService(_catalog, MediaTools.Discover()), new LibVlcPlayback());
        await Editor.LoadAsync();
    }

    public Task OpenSelectedAsync() => SelectedVideo is { } video ? OpenVideoAsync(video) : Task.CompletedTask;

    public Task<EditorExitDestination> RequestCloseEditorAsync(bool closeWindow = false)
    {
        _exitState.Request(closeWindow ? EditorExitDestination.Window : EditorExitDestination.Library);
        return CloseEditorAsync(discard: false);
    }

    public Task<EditorExitDestination> ConfirmCloseEditorAsync(bool discard) => CloseEditorAsync(discard);

    private async Task<EditorExitDestination> CloseEditorAsync(bool discard)
    {
        if (Editor is not { } editor) return _exitState.Complete();
        if (editor.IsBusy || _editorClosing) return EditorExitDestination.Stay;
        if (editor.IsDirty && !discard) { CloseWarning = true; return EditorExitDestination.Stay; }
        CloseWarning = false;
        _editorClosing = true;
        try { await editor.DisposeAsync(); Editor = null; }
        finally { _editorClosing = false; }
        return _exitState.Complete();
    }

    public void KeepEditing() { CloseWarning = false; _exitState.Cancel(); }

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
        if (IsBusy || HasEditor) return;
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
            SelectedVideo = null;
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
