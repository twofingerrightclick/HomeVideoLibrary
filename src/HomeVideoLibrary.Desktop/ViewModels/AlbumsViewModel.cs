using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>The album list, the album being shown and its "add videos" mode. Album changes go to the model; rows are
/// patched by id from each snapshot, so the selected row, its container and an open rename survive library changes.</summary>
public sealed class AlbumsViewModel : ViewModelBase
{
    private const string NewAlbumName = "New album";
    private readonly Func<LibraryModel?> _model;
    private readonly ShelfViewModel _shelf;
    private readonly Action<string> _toast;
    private readonly Action<string> _status;
    private readonly Action _viewChanged;
    private AlbumRowViewModel? _current;
    private bool _isAddingVideos;

    /// <param name="model">The open library's model, or null.</param>
    /// <param name="toast">Shows a short confirmation naming the count and the album.</param>
    /// <param name="status">Shows a failure on the status line.</param>
    /// <param name="viewChanged">Shows the model's current snapshot again (the shown album or add-mode changed, or an album
    /// was just created).</param>
    public AlbumsViewModel(Func<LibraryModel?> model, ShelfViewModel shelf, Action<string> toast, Action<string> status, Action viewChanged)
    {
        _model = model; _shelf = shelf; _toast = toast; _status = status; _viewChanged = viewChanged;
        ShowAllCommand = new RelayCommand(() => Current = null);
        CreateCommand = new AsyncCommand(_ => CreateAsync([]), () => _model() is not null, context: "Create album");
        CreateFromSelectionCommand = new AsyncCommand(_ => CreateAsync([.. _shelf.SelectedIds]), () => _model() is not null && _shelf.HasSelection,
            context: "Create album");
        RemoveSelectedCommand = new AsyncCommand(_ => RemoveSelectedAsync(), () => Current is not null && _shelf.HasSelection, context: "Remove from album");
        AddSelectedHereCommand = new AsyncCommand(_ => Current is { } album ? AddToAlbumAsync(album.Id, _shelf.SelectedIds) : Task.CompletedTask,
            () => Current is not null && _shelf.HasSelection, context: "Add to album");
        StartAddingCommand = new RelayCommand(() => IsAddingVideos = true, () => Current is not null && !IsAddingVideos);
        DoneAddingCommand = new RelayCommand(() => IsAddingVideos = false, () => IsAddingVideos);
        _shelf.SelectionChanged += UpdateCommands;
    }

    public ObservableCollection<AlbumRowViewModel> Rows { get; } = [];

    /// <summary>The album being shown, or null for all videos. User-selected in the album list.</summary>
    public AlbumRowViewModel? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(value, _current)) return;
            _current = value;
            _isAddingVideos = false;
            OnPropertyChanged(); OnPropertyChanged(nameof(IsAllVideos)); OnPropertyChanged(nameof(IsAddingVideos)); OnPropertyChanged(nameof(ViewTitle));
            UpdateCommands();
            _viewChanged();
        }
    }

    public bool IsAllVideos => Current is null;
    public bool CanStartAdding => Current is not null && !IsAddingVideos;
    public bool HasAlbums => Rows.Count > 0;

    /// <summary>Whether the shelf shows the videos outside <see cref="Current"/> so the user can pick some to add.</summary>
    public bool IsAddingVideos
    {
        get => _isAddingVideos;
        set
        {
            if (Current is null && value || !SetProperty(ref _isAddingVideos, value)) return;
            OnPropertyChanged(nameof(ViewTitle)); OnPropertyChanged(nameof(AddHereLabel));
            UpdateCommands();
            _viewChanged();
        }
    }

    public AlbumFilter Filter => Current is { } album ? new AlbumFilter(album.Id, IsAddingVideos) : AlbumFilter.All;
    public string ViewTitle => Current is { } album ? IsAddingVideos ? $"Choose videos to add to “{album.Name}”" : album.Name : "All videos";
    public string AddHereLabel => $"ADD {_shelf.SelectedCount}";

    public RelayCommand ShowAllCommand { get; }
    /// <summary>Creates an empty album with a free "New album" name and opens its inline rename.</summary>
    public AsyncCommand CreateCommand { get; }
    public AsyncCommand CreateFromSelectionCommand { get; }
    public AsyncCommand RemoveSelectedCommand { get; }
    public AsyncCommand AddSelectedHereCommand { get; }
    public RelayCommand StartAddingCommand { get; }
    public RelayCommand DoneAddingCommand { get; }

    /// <summary>Patches <see cref="Rows"/> to <paramref name="snapshot"/>'s albums by id and keeps <see cref="Current"/>
    /// on the same album; a deleted current album returns the view to all videos.</summary>
    public void Apply(LibrarySnapshot snapshot)
    {
        var albums = snapshot.Albums;
        var wanted = albums.Select(album => album.Id).ToHashSet();
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (!wanted.Contains(Rows[index].Id)) Rows.RemoveAt(index);
        for (var index = 0; index < albums.Length; index++)
        {
            var album = albums[index];
            var existing = IndexOf(album.Id, index);
            if (existing < 0) Rows.Insert(index, new AlbumRowViewModel(album, this));
            else if (existing != index) Rows.Move(existing, index);
            Rows[index].Update(album, snapshot.MembersOf(album.Id).Count);
        }
        if (_current is not null && !wanted.Contains(_current.Id)) Current = null;
        else OnPropertyChanged(nameof(ViewTitle));
        UpdateCommands();
    }

    private int IndexOf(Guid id, int start)
    {
        for (var index = start; index < Rows.Count; index++)
            if (Rows[index].Id == id) return index;
        return -1;
    }

    /// <summary>Adds videos to an album (a drop, the Add-to-album menu) and confirms with a toast naming both.</summary>
    public Task AddToAlbumAsync(Guid albumId, IEnumerable<Guid> videoIds)
    {
        var ids = videoIds.ToArray();
        if (ids.Length == 0) { _toast("Select videos first, then add them to an album."); return Task.CompletedTask; }
        return RunAsync(async model =>
        {
            var change = await Task.Run(() => model.AddToAlbumAsync(albumId, ids));
            _toast($"Added {Videos(change.Changed)} to “{change.Album.Name}”" + (change.Unchanged > 0 ? $"; {change.Unchanged} already there." : "."));
        });
    }

    internal Task AddSelectedToAsync(AlbumRowViewModel row) => AddToAlbumAsync(row.Id, _shelf.SelectedIds);

    internal Task RenameAsync(AlbumRowViewModel row, string name) => RunAsync(async model =>
    {
        await Task.Run(() => model.RenameAlbumAsync(row.Id, name));
        row.EndRename();
    });

    internal Task DeleteAsync(AlbumRowViewModel row) => RunAsync(async model =>
    {
        await Task.Run(() => model.DeleteAlbumAsync(row.Id));
        _toast($"Deleted album “{row.Name}”. Its videos stay in the library.");
    });

    internal bool HasSelection => _shelf.HasSelection;

    private Task CreateAsync(Guid[] ids) => RunAsync(async model =>
    {
        var taken = model.Snapshot.Albums.Select(album => AlbumNames.Key(album.Name)).ToHashSet(StringComparer.Ordinal);
        var name = NewAlbumName;
        for (var number = 2; taken.Contains(AlbumNames.Key(name)); number++) name = $"{NewAlbumName} {number}";
        var album = await Task.Run(() => model.CreateAlbumAsync(name, ids));
        // The model has published the album; showing the current snapshot now creates its row.
        _viewChanged();
        if (Rows.FirstOrDefault(row => row.Id == album.Id) is not { } created) return;
        Current = created;
        created.BeginRename();
        if (ids.Length > 0) _toast($"Created “{album.Name}” with {Videos(ids.Length)}.");
    });

    private Task RemoveSelectedAsync()
    {
        if (Current is not { } album) return Task.CompletedTask;
        var ids = _shelf.SelectedIds.ToArray();
        return RunAsync(async model =>
        {
            var change = await Task.Run(() => model.RemoveFromAlbumAsync(album.Id, ids));
            _toast($"Removed {Videos(change.Changed)} from “{change.Album.Name}”.");
        });
    }

    /// <summary>Runs an album change; model errors (a duplicate or empty name, a vanished album) are user-facing text.</summary>
    private async Task RunAsync(Func<LibraryModel, Task> action)
    {
        if (_model() is not { } model) return;
        try { await action(model); }
        catch (InvalidOperationException exception) { _status(exception.Message); }
    }

    private void UpdateCommands()
    {
        OnPropertyChanged(nameof(AddHereLabel)); OnPropertyChanged(nameof(CanStartAdding)); OnPropertyChanged(nameof(HasAlbums));
        foreach (var command in new[] { CreateCommand, CreateFromSelectionCommand, RemoveSelectedCommand, AddSelectedHereCommand })
            command.RaiseCanExecuteChanged();
        StartAddingCommand.RaiseCanExecuteChanged(); DoneAddingCommand.RaiseCanExecuteChanged();
        foreach (var row in Rows) row.UpdateCommands();
    }

    private static string Videos(int count) => count == 1 ? "1 video" : $"{count} videos";
}

/// <summary>One album in the drawer. The rename field exists only while <see cref="Rename"/> is set, so every rename
/// starts with a fresh text box.</summary>
public sealed class AlbumRowViewModel : ViewModelBase
{
    private readonly AlbumsViewModel _owner;
    private VideoAlbum _album;
    private int _count;
    private AlbumRenameViewModel? _rename;

    internal AlbumRowViewModel(VideoAlbum album, AlbumsViewModel owner)
    {
        _album = album; _owner = owner;
        RenameCommand = new RelayCommand(BeginRename, () => Rename is null);
        DeleteCommand = new AsyncCommand(_ => _owner.DeleteAsync(this), context: "Delete album");
        AddSelectedCommand = new AsyncCommand(_ => _owner.AddSelectedToAsync(this), () => _owner.HasSelection, context: "Add to album");
    }

    public Guid Id => _album.Id;
    public string Name => _album.Name;
    public int Count => _count;
    public AlbumRenameViewModel? Rename { get => _rename; private set { if (SetProperty(ref _rename, value)) { OnPropertyChanged(nameof(IsRenaming)); RenameCommand.RaiseCanExecuteChanged(); } } }
    public bool IsRenaming => Rename is not null;

    public RelayCommand RenameCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand AddSelectedCommand { get; }

    public void BeginRename() => Rename ??= new AlbumRenameViewModel(Name, name => _owner.RenameAsync(this, name), EndRename);

    internal void EndRename() => Rename = null;

    internal void Update(VideoAlbum album, int count)
    {
        if (!Equals(album, _album)) { _album = album; OnPropertyChanged(nameof(Name)); }
        if (count != _count) { _count = count; OnPropertyChanged(nameof(Count)); }
    }

    internal void UpdateCommands() => AddSelectedCommand.RaiseCanExecuteChanged();
}

/// <summary>An open inline rename: the draft name and its commit (Enter or leaving the field) and cancel (Esc).</summary>
public sealed class AlbumRenameViewModel : ViewModelBase
{
    private readonly string _original;
    private string _draftName;

    internal AlbumRenameViewModel(string name, Func<string, Task> commit, Action cancel)
    {
        _original = _draftName = name;
        CommitCommand = new AsyncCommand(_ => DraftName == _original ? Cancel(cancel) : commit(DraftName), context: "Rename album");
        CancelCommand = new RelayCommand(cancel);
    }

    public string DraftName { get => _draftName; set => SetProperty(ref _draftName, value); }
    public AsyncCommand CommitCommand { get; }
    public RelayCommand CancelCommand { get; }

    private static Task Cancel(Action cancel)
    {
        cancel();
        return Task.CompletedTask;
    }
}
