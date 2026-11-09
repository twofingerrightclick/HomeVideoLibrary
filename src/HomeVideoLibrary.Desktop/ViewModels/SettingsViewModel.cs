using System.Diagnostics;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Audio;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>Library settings: the library folder, refreshing the model from the MKVs (status and per-file errors), media
/// tool health and button sounds (persisted, off by default).</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly LibrarySettings _settings;
    private readonly IClickSoundService _sounds;
    private readonly IInteraction _interaction;
    private readonly Func<LibraryModel?> _model;
    private readonly Func<bool> _toolsReady;
    private readonly Func<string, Task> _openLibrary;
    private readonly Action<string> _status;
    private CancellationTokenSource? _indexCancellation;
    private Task? _indexRun;
    private IReadOnlyList<string> _indexErrors = [];
    private string _libraryRoot = "Choose a library folder to begin", _indexStatus = "", _toolHealth = "";
    private bool _isIndexing, _soundEffects;

    /// <param name="toolsReady">Whether every media tool answered; refreshing needs them.</param>
    /// <param name="openLibrary">Opens the library at a chosen folder.</param>
    public SettingsViewModel(LibrarySettings settings, IClickSoundService sounds, IInteraction interaction, Func<LibraryModel?> model,
        Func<bool> toolsReady, Func<string, Task> openLibrary, Action<string> status)
    {
        _settings = settings; _sounds = sounds; _interaction = interaction; _model = model; _toolsReady = toolsReady;
        _openLibrary = openLibrary; _status = status;
        ChooseFolderCommand = new AsyncCommand(_ => ChooseFolderAsync(), context: "Choose library");
        RefreshCommand = new AsyncCommand(_ => RefreshIndexAsync(), () => CanRefresh, context: "Refresh library");
        RebuildCommand = new AsyncCommand(_ => RefreshIndexAsync(force: true), () => CanRefresh, context: "Rebuild library");
        CancelIndexCommand = new RelayCommand(CancelIndex, () => IsIndexing);
    }

    public AsyncCommand ChooseFolderCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    /// <summary>Re-reads every MKV, not only changed ones.</summary>
    public AsyncCommand RebuildCommand { get; }
    public RelayCommand CancelIndexCommand { get; }

    public string LibraryRoot { get => _libraryRoot; internal set => SetProperty(ref _libraryRoot, value); }
    public string IndexStatus { get => _indexStatus; private set => SetProperty(ref _indexStatus, value); }
    /// <summary>Per-file problems from the last library refresh.</summary>
    public IReadOnlyList<string> IndexErrors { get => _indexErrors; private set { if (SetProperty(ref _indexErrors, value)) OnPropertyChanged(nameof(HasIndexErrors)); } }
    public bool HasIndexErrors => IndexErrors.Count > 0;
    public bool IsIndexing { get => _isIndexing; private set { if (SetProperty(ref _isIndexing, value)) UpdateCommands(); } }
    /// <summary>Which media tools are missing and what they disable, or that all are present.</summary>
    public string ToolHealth { get => _toolHealth; internal set => SetProperty(ref _toolHealth, value); }
    private bool CanRefresh => _model() is not null && !IsIndexing;

    /// <summary>Button click sounds. User-edited; applied at once and saved with the settings.</summary>
    public bool SoundEffects
    {
        get => _soundEffects;
        set
        {
            if (!SetProperty(ref _soundEffects, value)) return;
            _sounds.IsEnabled = value;
            _ = SaveSoundEffectsAsync(value);
        }
    }

    /// <summary>Applies the saved sound setting; enabling the service starts its audio device in the background.</summary>
    public async Task LoadAsync()
    {
        _soundEffects = await _settings.LoadSoundEffectsAsync();
        _sounds.IsEnabled = _soundEffects;
        OnPropertyChanged(nameof(SoundEffects));
    }

    private async Task SaveSoundEffectsAsync(bool enabled)
    {
        try { await _settings.SaveSoundEffectsAsync(enabled); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status("Button sounds changed, but the setting could not be saved: " + exception.Message);
        }
    }

    private async Task ChooseFolderAsync()
    {
        if (await _interaction.PickFolderAsync("Choose your video library folder") is { } path) await _openLibrary(path);
    }

    public void UpdateCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged(); RebuildCommand.RaiseCanExecuteChanged(); CancelIndexCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Reconciles the model with the files: reads new or changed MKVs (every MKV when <paramref name="force"/>).
    /// While a refresh is already running this waits for it instead.</summary>
    public Task RefreshIndexAsync(bool force = false)
    {
        if (IsIndexing && _indexRun is { } running) return running;
        return _indexRun = RunIndexAsync(force);
    }

    private async Task RunIndexAsync(bool force)
    {
        if (_model() is not { } model) return;
        if (!_toolsReady()) { IndexStatus = "Library refresh needs the missing media tools."; return; }
        using var cancellation = new CancellationTokenSource(); _indexCancellation = cancellation;
        IsIndexing = true;
        var progress = new Progress<IndexProgress>(update =>
        {
            if (model == _model()) IndexStatus = $"Reading {update.Completed}/{update.Total} · {update.VideoName}";
        });
        try
        {
            var result = await Task.Run(() => model.ReconcileAsync(force, progress, cancellation.Token));
            if (model != _model()) return;
            IndexErrors = result.Errors.Select(error => $"{error.VideoName}: {error.Message}").ToArray();
            IndexStatus = (result.Read == 0 ? "Library is current" : $"Library is current · read {result.Read}")
                + (result.Missing > 0 ? $" · {result.Missing} missing" : "") + (result.Errors.Count > 0 ? $" · {result.Errors.Count} unreadable" : "");
            if (result.Missing > 0 || result.Errors.Count > 0)
                _status((result.Missing > 0 ? $"{result.Missing} video file(s) are missing. " : "") +
                        (result.Errors.Count > 0 ? $"{result.Errors.Count} video(s) could not be read; see Settings." : ""));
        }
        catch (OperationCanceledException) { if (model == _model()) IndexStatus = "Refresh cancelled; completed entries kept. Refresh again to continue."; }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Library refresh failed: {exception}");
            if (model == _model()) IndexStatus = exception.Message;
        }
        finally { if (_indexCancellation == cancellation) { _indexCancellation = null; IsIndexing = false; } }
    }

    public void CancelIndex() => _indexCancellation?.Cancel();

    /// <summary>Cancels a running refresh and waits for it to end.</summary>
    public async Task StopIndexAsync()
    {
        CancelIndex();
        if (_indexRun is { } run) await run;
    }
}
