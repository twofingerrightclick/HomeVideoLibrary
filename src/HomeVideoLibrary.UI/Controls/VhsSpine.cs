using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>Printed sticker stock, modeled on the three spines in ui-reference/example stack of vhs.jpg.</summary>
public enum SpineSticker
{
    /// <summary>White lined label, thin "VIDEO CASSETTE" stripes at the left, brand block at the right.</summary>
    Branded,

    /// <summary>Graph-paper label with a brand block at the left.</summary>
    Graph,

    /// <summary>Cream lined label with a rounded "VIDEO CASSETTE" band at the right.</summary>
    Cream,
}

/// <summary>
/// One ~60px VHS tape spine as seen on a shelf. The sticker's printed artwork (brand marks, "VIDEO
/// CASSETTE" bands) is rotated a quarter turn the way a real spine label is printed, while the user's
/// handwritten <see cref="Label"/> reads horizontally and truncates with an ellipsis. Hover lifts the
/// tape out of the stack. Activation (click, Enter) raises <see cref="Activated"/>; selection is the
/// host's concern and is only displayed via <see cref="IsSelected"/>.
/// </summary>
[PseudoClasses(":selected", ":branded", ":graph", ":cream", ":has-leading")]
public class VhsSpine : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<VhsSpine, string?>(nameof(Label));

    /// <summary>Ink color of the sticker's printed bands; vary it by a stable video identity.</summary>
    public static readonly StyledProperty<IBrush?> AccentProperty =
        AvaloniaProperty.Register<VhsSpine, IBrush?>(nameof(Accent));

    public static readonly StyledProperty<SpineSticker> StickerProperty =
        AvaloniaProperty.Register<VhsSpine, SpineSticker>(nameof(Sticker));

    /// <summary>Short brand initials printed sideways on the sticker block (e.g. "SH", "TMT").</summary>
    public static readonly StyledProperty<string?> BrandTextProperty =
        AvaloniaProperty.Register<VhsSpine, string?>(nameof(BrandText), "VHS");

    /// <summary>Text molded into the shell's left end, readable horizontally (e.g. "E-180" or a duration).</summary>
    public static readonly StyledProperty<string?> ShellTextProperty =
        AvaloniaProperty.Register<VhsSpine, string?>(nameof(ShellText));

    /// <summary>Optional control placed on the shell's left end in place of the record tab (e.g. a selection check box).</summary>
    public static readonly StyledProperty<object?> LeadingContentProperty =
        AvaloniaProperty.Register<VhsSpine, object?>(nameof(LeadingContent));

    public static readonly StyledProperty<bool> IsSelectedProperty =
        AvaloniaProperty.Register<VhsSpine, bool>(nameof(IsSelected));

    public static readonly RoutedEvent<RoutedEventArgs> ActivatedEvent =
        RoutedEvent.Register<VhsSpine, RoutedEventArgs>(nameof(Activated), RoutingStrategies.Bubble);

    static VhsSpine()
    {
        FocusableProperty.OverrideDefaultValue<VhsSpine>(true);
    }

    public VhsSpine()
    {
        UpdateStickerClasses();
    }

    public event EventHandler<RoutedEventArgs>? Activated
    {
        add => AddHandler(ActivatedEvent, value);
        remove => RemoveHandler(ActivatedEvent, value);
    }

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public IBrush? Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public SpineSticker Sticker
    {
        get => GetValue(StickerProperty);
        set => SetValue(StickerProperty, value);
    }

    public string? BrandText
    {
        get => GetValue(BrandTextProperty);
        set => SetValue(BrandTextProperty, value);
    }

    public string? ShellText
    {
        get => GetValue(ShellTextProperty);
        set => SetValue(ShellTextProperty, value);
    }

    public object? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public bool IsSelected
    {
        get => GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StickerProperty)
        {
            UpdateStickerClasses();
        }
        else if (change.Property == IsSelectedProperty)
        {
            PseudoClasses.Set(":selected", change.GetNewValue<bool>());
        }
        else if (change.Property == LeadingContentProperty)
        {
            PseudoClasses.Set(":has-leading", change.NewValue is not null);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left
            && e.KeyModifiers == KeyModifiers.None
            && new Rect(Bounds.Size).Contains(e.GetPosition(this)))
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(ActivatedEvent));
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Enter && !e.Handled)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(ActivatedEvent));
        }
    }

    private void UpdateStickerClasses()
    {
        PseudoClasses.Set(":branded", Sticker == SpineSticker.Branded);
        PseudoClasses.Set(":graph", Sticker == SpineSticker.Graph);
        PseudoClasses.Set(":cream", Sticker == SpineSticker.Cream);
    }
}
