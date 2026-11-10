using HomeVideoLibrary.UI.Controls;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>How a tape's spine sticker looks: a stable pick from the VCR sticker stock and accent palette, so a tape
/// keeps its look across launches. <see cref="AccentKey"/> names a brush resource from the UI library.</summary>
public sealed record SpineStyle(SpineSticker Sticker, string AccentKey, string BrandText)
{
    private static readonly SpineSticker[] Stickers = [SpineSticker.Branded, SpineSticker.Graph, SpineSticker.Cream];
    private static readonly string[] Accents =
        ["Red", "Green", "Yellow", "Blue", "Purple", "Brown"];
    private static readonly string[] Brands = ["SH", "TMT", "HG", "EQ", "XR", "ST"];

    public static SpineStyle For(Guid id)
    {
        // Guid bytes are random for v4 ids; separate bytes keep the three choices independent.
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return new SpineStyle(Stickers[bytes[0] % Stickers.Length], "Vcr.Brush.Spine." + Accents[bytes[1] % Accents.Length],
            Brands[bytes[2] % Brands.Length]);
    }
}
