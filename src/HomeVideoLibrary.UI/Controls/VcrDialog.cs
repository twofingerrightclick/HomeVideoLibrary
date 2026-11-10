using Avalonia;
using Avalonia.Controls;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// Dialog chrome: a raised faceplate with a printed title strip, the body in
/// <see cref="ContentControl.Content"/>, and a recessed footer rail for <see cref="Footer"/>
/// (typically right-aligned keys). Host it as the content of a <c>Window</c> or an overlay.
/// </summary>
public class VcrDialog : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<VcrDialog, string?>(nameof(Title));

    public static readonly StyledProperty<object?> FooterProperty =
        AvaloniaProperty.Register<VcrDialog, object?>(nameof(Footer));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
