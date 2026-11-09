using System.Collections.ObjectModel;
using System.Globalization;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class ClipEditorViewModel : ViewModelBase, IDisposable
{
    private readonly IClipEditingService _editing;
    private readonly LibrarySearchIndex? _index;
    private CancellationTokenSource? _cancellation;
    private string _title, _start = "0", _end, _split, _status = "Choose an operation. Every save creates new MKV files.";
    private bool _isBusy;
    private double _progress;
    private LibraryVideo? _selectedSource;
    private string _formatSummary = "Inspecting source output format…";
    private CancellationTokenSource? _formatCancellation;
    private long _formatVersion;
    private bool _disposed;

    public ClipEditorViewModel(IReadOnlyList<LibraryVideo> sources, IClipEditingService editing, LibrarySearchIndex? index = null)
    {
        if (sources.Count == 0) throw new ArgumentException("Select at least one video.", nameof(sources));
        Sources = new ObservableCollection<LibraryVideo>(sources); _editing = editing; _index = index;
        _title = sources.Count > 1 ? "Merged home video" : sources[0].Title.Length > 0 ? sources[0].Title : sources[0].DisplayName;
        _end = sources[0].Duration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
        _split = (sources[0].Duration.TotalSeconds / 2).ToString("0.###", CultureInfo.InvariantCulture);
    }
    public ObservableCollection<LibraryVideo> Sources { get; }
    public bool IsSingle => Sources.Count == 1;
    public bool CanMerge => Sources.Count >= 2 && !IsBusy;
    public string OutputTitle { get => _title; set => SetProperty(ref _title, value); }
    public string StartSeconds { get => _start; set => SetProperty(ref _start, value); }
    public string EndSeconds { get => _end; set => SetProperty(ref _end, value); }
    public string SplitSeconds { get => _split; set => SetProperty(ref _split, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string OutputFormatSummary { get => _formatSummary; private set => SetProperty(ref _formatSummary, value); }
    public bool IsBusy { get => _isBusy; private set { SetProperty(ref _isBusy, value); OnPropertyChanged(nameof(CanMerge)); } }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public LibraryVideo? SelectedSource { get => _selectedSource; set => SetProperty(ref _selectedSource, value); }
    public void MoveSelected(int direction)
    {
        if (IsBusy || SelectedSource is not { } source) return;
        var current = Sources.IndexOf(source); var target = current + direction;
        if (target >= 0 && target < Sources.Count) Sources.Move(current, target);
        SelectedSource = source;
        _ = LoadFormatAsync();
    }
    public async Task LoadFormatAsync()
    {
        _formatCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource(); _formatCancellation = cancellation;
        var version = ++_formatVersion; var first = Sources[0];
        try
        {
            var format = await Task.Run(() => _editing.GetSourceFormatAsync(first, cancellation.Token));
            if (_disposed || version != _formatVersion) return;
            OutputFormatSummary = $"{format.Width} × {format.Height} · sample aspect {format.SampleAspectRatio} · H.264 CRF 18 / stereo AAC. " +
                (IsSingle ? $"Source frame timing retained ({format.FrameRate} fps average)." : $"Merge uses first clip's {format.FrameRate} fps; smaller sources are padded without upscaling.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed && version == _formatVersion) OutputFormatSummary = "Cannot inspect output format: " + exception.Message; }
        finally { if (_formatCancellation == cancellation) _formatCancellation = null; }
    }
    public Task TrimAsync() => RunAsync((progress, token) => _editing.TrimAsync(Sources[0], ParsePosition(StartSeconds), ParsePosition(EndSeconds), OutputTitle, progress, token));
    public Task SplitAsync() => RunAsync((progress, token) => _editing.SplitAsync(Sources[0], ParsePosition(SplitSeconds), OutputTitle, progress, token));
    public Task MergeAsync() => RunAsync((progress, token) => _editing.MergeAsync(Sources.ToArray(), OutputTitle, progress, token));
    private async Task RunAsync(Func<IProgress<ClipEditProgress>, CancellationToken, Task<IReadOnlyList<LibraryVideo>>> operation)
    {
        if (IsBusy) return;
        IsBusy = true; Progress = 0;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        var progress = new Progress<ClipEditProgress>(update => { Status = update.Stage; Progress = (update.Fraction ?? 0) * 100; });
        try
        {
            var outputs = await Task.Run(() => operation(progress, cancellation.Token));
            var warnings = new List<string>();
            if (_index is { } index)
                foreach (var video in outputs)
                {
                    try { await Task.Run(() => index.UpdateVideoAsync(video, cancellationToken: cancellation.Token)); }
                    catch (Exception exception) { warnings.Add(exception.Message); }
                }
            Status = $"Saved {outputs.Count} new video(s) in the library. Originals unchanged." + (warnings.Count > 0 ? " Search refresh needed: " + string.Join(" ", warnings) : "");
            Progress = 100;
        }
        catch (OperationCanceledException) { Status = "Editing cancelled; no new outputs published. Originals unchanged."; }
        catch (Exception exception) { Status = exception.Message; }
        finally { _cancellation = null; IsBusy = false; }
    }
    private static TimeSpan ParsePosition(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
        && double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
        ? TimeSpan.FromSeconds(seconds) : throw new ArgumentException("Enter a finite, nonnegative position in seconds.");
    public void Cancel() => _cancellation?.Cancel();
    public void Dispose() { _disposed = true; Cancel(); _formatCancellation?.Cancel(); }
}
