using Avalonia.Controls;
using HomeVideoLibrary.Gallery.Sections;

namespace HomeVideoLibrary.Gallery;

/// <param name="Key">File-name-safe id, used for screenshot output.</param>
public sealed record GallerySection(string Key, string Title, Func<Control> Create)
{
    public static IReadOnlyList<GallerySection> All { get; } =
    [
        new("buttons", "Buttons", () => new ButtonsSection()),
        new("meters", "Volume & LCD", () => new MetersSection()),
    ];
}
