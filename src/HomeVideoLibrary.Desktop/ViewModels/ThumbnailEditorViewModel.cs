using Avalonia.Media.Imaging;
using HomeVideoLibrary.Core;

namespace HomeVideoLibrary.Desktop.ViewModels;

public sealed class ThumbnailEditorViewModel(VideoThumbnail thumbnail) : IDisposable
{
    public VideoThumbnail Thumbnail { get; } = thumbnail;
    public TimeSpan Position => Thumbnail.Position;
    public Bitmap Image { get; } = new(thumbnail.ImagePath);
    public void Dispose() => Image.Dispose();
}
