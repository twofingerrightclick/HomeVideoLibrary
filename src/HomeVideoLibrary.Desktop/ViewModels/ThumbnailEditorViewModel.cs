using Avalonia.Media.Imaging;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>A chosen thumbnail whose preview decodes off the UI thread at inspector size. Create it on the UI thread.</summary>
public sealed class ThumbnailEditorViewModel : ViewModelBase, IDisposable
{
    private const int PreviewWidth = 320;
    private Bitmap? _image;
    private bool _disposed;

    public ThumbnailEditorViewModel(VideoThumbnail thumbnail)
    {
        Thumbnail = thumbnail;
        _ = LoadImageAsync();
    }

    public VideoThumbnail Thumbnail { get; }
    public TimeSpan Position => Thumbnail.Position;
    public Bitmap? Image { get => _image; private set => SetProperty(ref _image, value); }

    private async Task LoadImageAsync()
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(Thumbnail.ImagePath);
                return Bitmap.DecodeToWidth(stream, PreviewWidth);
            });
            if (_disposed) bitmap.Dispose();
            else Image = bitmap;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning($"Thumbnail preview {Thumbnail.ImagePath} could not be shown: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Image?.Dispose();
        Image = null;
    }
}
