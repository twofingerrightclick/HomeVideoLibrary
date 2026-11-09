using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.Desktop.Playback;
using Avalonia.Threading;

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
    private AlbumRepository? _albumRepository;
    private LibrarySearchIndex? _searchIndex;
    private CancellationTokenSource? _indexCancellation, _searchCancellation;
    private long _libraryGeneration, _queryVersion;
    private bool _isIndexing, _isChoosingAlbumVideos;
    private string _searchText = "", _albumName = "", _indexStatus = "";
    private VideoAlbum? _currentAlbum, _targetAlbum;
    private IReadOnlyList<LibraryVideo> _selectedVideos = [];
    private ClipEditorViewModel? _clipEditor;

    public ObservableCollection<LibraryVideo> Videos { get; } = [];
    public ObservableCollection<VideoAlbum> Albums { get; } = [];
    public string SearchText { get => _searchText; set { if (SetProperty(ref _searchText, value)) _ = RefreshVideosAsync(debounce: true); } }
    public string AlbumName { get => _albumName; set => SetProperty(ref _albumName, value); }
    public string IndexStatus { get => _indexStatus; private set => SetProperty(ref _indexStatus, value); }
    public bool IsIndexing { get => _isIndexing; private set => SetProperty(ref _isIndexing, value); }
    public VideoAlbum? CurrentAlbum
    {
        get => _currentAlbum;
        set
        {
            if (!SetProperty(ref _currentAlbum, value)) return;
            AlbumName = value?.Name ?? ""; IsChoosingAlbumVideos = false;
            OnPropertyChanged(nameof(HasAlbum)); OnPropertyChanged(nameof(CurrentViewTitle));
            _ = RefreshVideosAsync();
        }
    }
    public VideoAlbum? TargetAlbum { get => _targetAlbum; set { SetProperty(ref _targetAlbum, value); OnPropertyChanged(nameof(CanAddSelected)); } }
    public bool HasAlbum => CurrentAlbum is not null;
    public bool IsChoosingAlbumVideos { get => _isChoosingAlbumVideos; private set { SetProperty(ref _isChoosingAlbumVideos, value); OnPropertyChanged(nameof(CurrentViewTitle)); } }
    public string CurrentViewTitle => IsChoosingAlbumVideos ? $"Add videos to {CurrentAlbum?.Name}" : CurrentAlbum?.Name ?? "All videos";
    public IReadOnlyList<LibraryVideo> SelectedVideos => _selectedVideos;
    public bool CanAddSelected => TargetAlbum is not null && SelectedVideos.Count > 0 && !IsBusy;
    public void SetSelectedVideos(IEnumerable<LibraryVideo> videos) { _selectedVideos = videos.ToArray(); OnPropertyChanged(nameof(CanAddSelected)); OnPropertyChanged(nameof(CanEditClips)); }
    public string LibraryRoot { get => _libraryRoot; private set => SetProperty(ref _libraryRoot, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(CanOpenSelected)); OnPropertyChanged(nameof(CanAddSelected)); OnPropertyChanged(nameof(CanEditClips)); } }
    public bool CanImport => _catalog is not null && !IsBusy;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public VideoEditorViewModel? Editor { get => _editor; private set { SetProperty(ref _editor, value); OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(HasVideoEditor)); OnPropertyChanged(nameof(CanOpenSelected)); OnPropertyChanged(nameof(CanEditClips)); } }
    public ClipEditorViewModel? ClipEditor { get => _clipEditor; private set { SetProperty(ref _clipEditor, value); OnPropertyChanged(nameof(HasEditor)); OnPropertyChanged(nameof(HasClipEditor)); OnPropertyChanged(nameof(CanOpenSelected)); OnPropertyChanged(nameof(CanEditClips)); } }
    public bool HasVideoEditor => Editor is not null;
    public bool HasClipEditor => ClipEditor is not null;
    public bool CanEditClips => SelectedVideos.Count > 0 && !HasEditor && !IsBusy;
    public LibraryVideo? SelectedVideo { get => _selectedVideo; set { SetProperty(ref _selectedVideo, value); OnPropertyChanged(nameof(CanOpenSelected)); } }
    public bool CanOpenSelected => SelectedVideo is not null && !IsBusy && !HasEditor;
    public bool HasEditor => Editor is not null || ClipEditor is not null;
    public bool CloseWarning { get => _closeWarning; private set => SetProperty(ref _closeWarning, value); }

    public async Task OpenVideoAsync(LibraryVideo video)
    {
        if (_catalog is null || IsBusy || HasEditor) return;
        var metadata = new MatroskaMetadataService(_catalog, MediaTools.Discover());
        IVideoMetadataService service = metadata;
        if (_searchIndex is { } index)
        {
            var indexed = new IndexedMetadataService(metadata, index);
            indexed.IndexingFailed += warning => Dispatcher.UIThread.Post(() => Status = warning);
            service = indexed;
        }
        Editor = new VideoEditorViewModel(video, _catalog.GetVideoPath(video.RelativePath), service, new LibVlcPlayback());
        await Editor.LoadAsync();
    }

    public Task OpenSelectedAsync() => SelectedVideo is { } video ? OpenVideoAsync(video) : Task.CompletedTask;
    public async Task OpenClipEditorAsync()
    {
        if (!CanEditClips || _catalog is null) return;
        ClipEditor = new ClipEditorViewModel(SelectedVideos.ToArray(), new ClipEditingService(_catalog, MediaTools.Discover()), _searchIndex);
        await ClipEditor.LoadFormatAsync();
    }

    public Task<EditorExitDestination> RequestCloseEditorAsync(bool closeWindow = false)
    {
        _exitState.Request(closeWindow ? EditorExitDestination.Window : EditorExitDestination.Library);
        return CloseEditorAsync(discard: false);
    }

    public Task<EditorExitDestination> ConfirmCloseEditorAsync(bool discard) => CloseEditorAsync(discard);

    private async Task<EditorExitDestination> CloseEditorAsync(bool discard)
    {
        if (ClipEditor is { } clips)
        {
            if (clips.IsBusy) return EditorExitDestination.Stay;
            clips.Dispose(); ClipEditor = null; await RefreshVideosAsync();
            return _exitState.Complete();
        }
        if (Editor is not { } editor) return _exitState.Complete();
        if (editor.IsBusy || _editorClosing) return EditorExitDestination.Stay;
        if (editor.IsDirty && !discard) { CloseWarning = true; return EditorExitDestination.Stay; }
        CloseWarning = false;
        _editorClosing = true;
        try { await editor.DisposeAsync(); Editor = null; await RefreshVideosAsync(); }
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
        CancelIndex(); _indexCancellation = null; IsIndexing = false; _searchCancellation?.Cancel();
        var generation = ++_libraryGeneration;
        IsBusy = true;
        try
        {
            var catalog = new LibraryCatalog(new LibraryPaths(root));
            await Task.Run(() => catalog.InitializeAsync());
            await _settings.SaveRootAsync(root);
            _catalog = catalog;
            _albumRepository = new AlbumRepository(catalog);
            _searchIndex = new LibrarySearchIndex(catalog, new MatroskaMetadataService(catalog, MediaTools.Discover()));
            LibraryRoot = catalog.Paths.RootDirectory;
            _currentAlbum = null; _isChoosingAlbumVideos = false; _searchText = "";
            OnPropertyChanged(nameof(CurrentAlbum)); OnPropertyChanged(nameof(HasAlbum)); OnPropertyChanged(nameof(CurrentViewTitle)); OnPropertyChanged(nameof(SearchText));
            Videos.Clear();
            SelectedVideo = null;
            await ReloadAlbumsAsync();
            await RefreshVideosAsync();
            if (generation == _libraryGeneration) Status = $"{Videos.Count} videos in your library";
        }
        catch (Exception exception) { Status = exception.Message; }
        finally { IsBusy = false; }
        _ = RefreshIndexAsync();
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
                    imported++;
                    if (_searchIndex is { } index)
                    {
                        try { await Task.Run(() => index.UpdateVideoAsync(video, cancellationToken: cancellation.Token)); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception) { errors.Add($"{video.DisplayName} imported; search refresh needed: {exception.Message}"); }
                    }
                    await RefreshVideosAsync();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception) { errors.Add($"{Path.GetFileName(source)}: {exception.Message}"); }
            }
            Status = errors.Count == 0 ? $"Imported {imported} video(s)." : $"Imported {imported}; {errors.Count} issue(s). {string.Join(Environment.NewLine, errors)}";
        }
        catch (OperationCanceledException) { Status = $"Import cancelled. {imported} completed video(s) kept; originals unchanged."; }
        finally { _importCancellation = null; IsBusy = false; await RefreshVideosAsync(); }
    }

    public void CancelImport() => _importCancellation?.Cancel();

    public async Task RefreshVideosAsync(bool debounce = false)
    {
        var index = _searchIndex;
        if (index is null) return;
        _searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _searchCancellation = cancellation;
        var version = ++_queryVersion; var generation = _libraryGeneration;
        var query = SearchText; var album = CurrentAlbum?.Id; var exclude = IsChoosingAlbumVideos;
        try
        {
            if (debounce) await Task.Delay(160, cancellation.Token);
            var videos = await Task.Run(() => index.SearchAsync(query, album, exclude, cancellation.Token));
            if (version != _queryVersion || generation != _libraryGeneration) return;
            Videos.Clear(); SelectedVideo = null; SetSelectedVideos([]);
            foreach (var video in videos) Videos.Add(video);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (version == _queryVersion && generation == _libraryGeneration) Status = exception.Message; }
        finally { if (_searchCancellation == cancellation) _searchCancellation = null; }
    }

    public async Task RefreshIndexAsync(bool force = false)
    {
        if (_searchIndex is not { } index || IsIndexing) return;
        var generation = _libraryGeneration;
        using var cancellation = new CancellationTokenSource(); _indexCancellation = cancellation;
        IsIndexing = true;
        var errors = 0;
        var progress = new Progress<IndexProgress>(update =>
        {
            if (generation != _libraryGeneration) return;
            if (update.Error is not null) { errors++; Status = $"Could not index {update.VideoName}: {update.Error}"; }
            IndexStatus = $"Indexed {update.Completed}/{update.Total} · {update.VideoName}";
        });
        try
        {
            await Task.Run(() => index.RefreshAsync(force, progress, cancellation.Token));
            if (generation == _libraryGeneration) { IndexStatus = errors == 0 ? "Search index is current" : $"Refresh finished with {errors} file errors"; await RefreshVideosAsync(); }
        }
        catch (OperationCanceledException) { if (generation == _libraryGeneration) IndexStatus = "Refresh cancelled; completed entries kept. Refresh again to continue."; }
        catch (Exception exception) { if (generation == _libraryGeneration) IndexStatus = exception.Message; }
        finally { if (_indexCancellation == cancellation) { _indexCancellation = null; IsIndexing = false; } }
    }

    public void CancelIndex() => _indexCancellation?.Cancel();
    private async Task ReloadAlbumsAsync()
    {
        if (_albumRepository is not { } albums) return;
        var generation = _libraryGeneration;
        var list = await Task.Run(() => albums.ListAsync());
        if (generation != _libraryGeneration) return;
        var current = CurrentAlbum?.Id; var target = TargetAlbum?.Id;
        Albums.Clear(); foreach (var album in list) Albums.Add(album);
        CurrentAlbum = list.FirstOrDefault(album => album.Id == current);
        TargetAlbum = list.FirstOrDefault(album => album.Id == target) ?? list.FirstOrDefault();
    }

    public Task CreateAlbumAsync()
    {
        var selectedIds = SelectedVideos.Select(video => video.Id).ToArray();
        return AlbumActionAsync(async albums => { var album = await albums.CreateAsync(AlbumName, selectedIds); await ReloadAlbumsAsync(); CurrentAlbum = Albums.First(item => item.Id == album.Id); });
    }
    public Task RenameAlbumAsync() => CurrentAlbum is { } album ? AlbumActionAsync(async albums => { await albums.RenameAsync(album.Id, AlbumName); await ReloadAlbumsAsync(); }) : Task.CompletedTask;
    public Task DeleteAlbumAsync() => CurrentAlbum is { } album ? AlbumActionAsync(async albums => { await albums.DeleteAsync(album.Id); await ReloadAlbumsAsync(); await RefreshVideosAsync(); Status = "Album removed. All video files remain in the library."; }) : Task.CompletedTask;
    public Task AddSelectedToAlbumAsync() => TargetAlbum is { } album ? AddToAlbumAsync(album.Id, SelectedVideos.Select(video => video.Id)) : Task.CompletedTask;
    public Task AddSelectedToCurrentAsync() => CurrentAlbum is { } album ? AddToAlbumAsync(album.Id, SelectedVideos.Select(video => video.Id)) : Task.CompletedTask;
    public Task RemoveSelectedFromCurrentAsync() => CurrentAlbum is { } album ? AlbumActionAsync(async albums => { await albums.RemoveVideosAsync(album.Id, SelectedVideos.Select(video => video.Id).ToArray()); await RefreshVideosAsync(); }) : Task.CompletedTask;
    public Task AddToAlbumAsync(Guid albumId, IEnumerable<Guid> videoIds)
    {
        var ids = videoIds.ToArray();
        return AlbumActionAsync(async albums => { await albums.AddVideosAsync(albumId, ids); await RefreshVideosAsync(); Status = "Videos added to album."; });
    }
    public void ShowLibrary() { IsChoosingAlbumVideos = false; CurrentAlbum = null; _ = RefreshVideosAsync(); }
    public void ToggleAlbumAddMode() { if (!HasAlbum) return; IsChoosingAlbumVideos = !IsChoosingAlbumVideos; _ = RefreshVideosAsync(); }
    private async Task AlbumActionAsync(Func<AlbumRepository, Task> action)
    {
        if (_albumRepository is not { } albums || IsBusy || HasEditor) return;
        IsBusy = true;
        try { await action(albums); }
        catch (Exception exception) { Status = exception.Message; }
        finally { IsBusy = false; }
    }
}
