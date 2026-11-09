using Avalonia.Controls;
using HomeVideoLibrary.Gallery.Sections;

namespace HomeVideoLibrary.Gallery;

/// <param name="Key">File-name-safe id, used for screenshot output.</param>
public sealed record GallerySection(string Key, string Title, Func<Control> Create)
{
    public static IReadOnlyList<GallerySection> All { get; } =
    [
        new("buttons", "Buttons", () => new ButtonsSection()),
        new("inputs", "Inputs & surfaces", () => new InputsSection()),
        new("tabs", "Inspector tabs", () => new TabsSection()),
        new("scrub", "Scrub bar", () => new ScrubSection()),
        new("tags", "Tag chips", () => new TagsSection()),
    ];
}
