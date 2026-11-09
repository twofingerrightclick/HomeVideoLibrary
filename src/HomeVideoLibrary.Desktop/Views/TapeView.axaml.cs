using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using HomeVideoLibrary.Desktop.ViewModels;

namespace HomeVideoLibrary.Desktop.Views;

public sealed partial class TapeView : UserControl
{
    private TapePreviewSession? _session;
    private CancellationTokenSource? _request;
    private int _bucket = -1;
    private long _version;
    private Bitmap? _current, _previous;
    public TapeView()
    {
        InitializeComponent();
        PointerExited += (_, _) => StopPreview();
        LabelSurface.PointerExited += (_, _) => StopPreview();
        LabelSurface.PointerMoved += PreviewMoved;
        DetachedFromVisualTree += (_, _) => StopPreview();
        DataContextChanged += (_, _) => StopPreview();
    }
    private async void PreviewMoved(object? sender, PointerEventArgs args)
    {
        if (DataContext is not TapeViewModel tape || args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { StopPreview(); return; }
        var x = args.GetPosition(LabelSurface).X;
        PreviewPopup.HorizontalOffset = Math.Clamp(x - LabelSurface.Bounds.Width / 2, -LabelSurface.Bounds.Width / 2 + 104, LabelSurface.Bounds.Width / 2 - 104);
        var bucket = Math.Clamp((int)(x / Math.Max(1, LabelSurface.Bounds.Width) * 10), 0, 9);
        if (bucket == _bucket) return;
        _bucket = bucket;
        _request?.Cancel();
        var cancellation = new CancellationTokenSource(); _request = cancellation;
        var version = ++_version;
        try
        {
            if (_session is null)
            {
                var service = (this.FindAncestorOfType<MainWindow>()?.DataContext as MainWindowViewModel)?.CreatePreviewService();
                if (service is null) return;
                _session = new TapePreviewSession(service, tape.Video);
            }
            var frame = await _session.GetAsync(bucket, cancellation.Token);
            if (version != _version || cancellation.IsCancellationRequested) return;
            using var stream = File.OpenRead(frame.ImagePath);
            var bitmap = Bitmap.DecodeToWidth(stream, 416);
            _previous?.Dispose(); _previous = _current; _current = bitmap;
            PreviousPreview.Source = _previous; CurrentPreview.Opacity = 0; CurrentPreview.Source = _current;
            PreviewTime.Text = frame.Position.ToString(@"hh\:mm\:ss");
            PreviewPopup.IsOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (version == _version) CurrentPreview.Opacity = 1; }, Avalonia.Threading.DispatcherPriority.Render);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (version == _version) PreviewPopup.IsOpen = false; }
        finally { if (_request == cancellation) _request = null; cancellation.Dispose(); }
    }
    private void StopPreview()
    {
        ++_version; _bucket = -1; _request?.Cancel(); _request = null;
        _session?.Dispose(); _session = null; PreviewPopup.IsOpen = false;
        CurrentPreview.Source = null; PreviousPreview.Source = null;
        _current?.Dispose(); _previous?.Dispose(); _current = null; _previous = null;
    }
}
