using Avalonia.Media.Imaging;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>A chosen thumbnail. Its preview decodes off the UI thread at inspector size the first time
/// <see cref="Image"/> is read, so frames never shown (the FRAMES tab closed) are never decoded. Create it and read
/// <see cref="Image"/> on the UI thread.</summary>
public sealed class ThumbnailEditorViewModel(VideoThumbnail thumbnail) : ViewModelBase, IDisposable
{
    private const int PreviewWidth = 320;
    private Bitmap? _image;
    private bool _loading, _disposed;

    public VideoThumbnail Thumbnail { get; } = thumbnail;
    public TimeSpan Position => Thumbnail.Position;
    public string PositionText => Timecode.Format(Thumbnail.Position);

    public Bitmap? Image
    {
        get
        {
            if (!_loading && !_disposed)
            {
                _loading = true;
                _ = LoadImageAsync();
            }
            return _image;
        }
    }

    private async Task LoadImageAsync()
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(Thumbnail.ImagePath);
                return Bitmap.DecodeToWidth(stream, PreviewWidth);
            });
            if (_disposed) { bitmap.Dispose(); return; }
            _image = bitmap;
            OnPropertyChanged(nameof(Image));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning($"Thumbnail preview {Thumbnail.ImagePath} could not be shown: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _image?.Dispose();
        _image = null;
        OnPropertyChanged(nameof(Image));
    }
}
