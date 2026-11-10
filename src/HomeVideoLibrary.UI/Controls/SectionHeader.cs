using Avalonia;
using Avalonia.Controls;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Silkscreened group title with a molded groove running to the right edge, like the bracketed
/// "LOOP" and "TRACK SEARCH" groupings on a deck faceplate. <see cref="ContentControl.Content"/> sits at
/// the far right for the group's actions (e.g. an add key).
/// </summary>
public class SectionHeader : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SectionHeader, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}
