using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using HomeVideoLibrary.UI.Logic;

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
/// handwritten <see cref="Label"/> reads horizontally. A label too long for one line at the design size shrinks,
/// down to <see cref="MinLabelFontSize"/> on screen, then wraps to two lines, then truncates with an ellipsis. The spine keeps a
/// real VHS spine's proportions (187 x 25 mm) at any size: it takes the available width and the height
/// that ratio implies (or the reverse when only a height is given), and its face scales uniformly. Hover
/// brings the tape slightly forward (scale and shadow only; it never shifts). As a <see cref="Button"/> it opens the tape through <c>Command</c> (or
/// <c>Click</c>) on a single click, Enter, or Space. Double-clicks open it once, and modifier clicks are
/// left to the host list for selection, which the host shows (on the shelf, a check box in
/// <see cref="LeadingContent"/>).
/// </summary>
[PseudoClasses(":branded", ":graph", ":cream", ":has-leading")]
public class VhsSpine : Button
{
    /// <summary>Length to height of a VHS cassette spine (187 x 25 mm).</summary>
    public const double AspectRatio = 187.0 / 25.0;

    /// <summary>The face is designed at this height and scaled uniformly.</summary>
    public const double DesignHeight = 60;

    public const double DesignWidth = DesignHeight * AspectRatio;

    /// <summary>Smallest on-screen size (device-independent px) the handwritten label shrinks to before it wraps.</summary>
    public const double MinLabelFontSize = 24;

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

    /// <summary>When set, a click first plays the tape coming out of the shelf toward the viewer (a short scale-up and
    /// fade), then opens it. Hosts turn it off for reduced motion.</summary>
    public static readonly StyledProperty<bool> OpenTransitionProperty =
        AvaloniaProperty.Register<VhsSpine, bool>(nameof(OpenTransition));

    private static readonly Animation ComeOut = new()
    {
        Duration = TimeSpan.FromMilliseconds(180),
        Easing = new CubicEaseIn(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d), new Setter(RenderTransformProperty, TransformOperations.Parse("scale(1.025)")) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0d), new Setter(RenderTransformProperty, TransformOperations.Parse("scale(1.12)")) } },
        },
    };

    // Label fits are shared across spines: a virtualized shelf re-arranges the same titles at the same size while it
    // scrolls. Bounded because a window resize produces a new scale on every step.
    private static readonly Dictionary<LabelSlot, LabelFit> LabelFits = [];
    private const int MaxLabelFits = 4096;

    private Border? _shell;
    private TextBlock? _label;
    private LabelSlot? _fittedSlot;
    private Task _opening = Task.CompletedTask;

    public VhsSpine()
    {
        UpdateStickerClasses();
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

    public bool OpenTransition
    {
        get => GetValue(OpenTransitionProperty);
        set => SetValue(OpenTransitionProperty, value);
    }

    /// <summary>Fits the largest spine of the right proportions into the available space; with no constraint it
    /// takes its design size.</summary>
    public static Size FitSize(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width
            : double.IsFinite(available.Height) ? available.Height * AspectRatio : DesignWidth;
        var height = width / AspectRatio;
        if (double.IsFinite(available.Height) && height > available.Height)
        {
            height = available.Height;
            width = height * AspectRatio;
        }

        return new Size(width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = FitSize(availableSize);
        base.MeasureOverride(size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = FitSize(finalSize);
        var arranged = base.ArrangeOverride(size);
        FitLabel(size.Height / DesignHeight);
        return arranged;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StickerProperty)
        {
            UpdateStickerClasses();
        }
        else if (change.Property == LabelProperty)
        {
            // The label's own re-measure stops at the fixed-size design face, so the fit would not otherwise rerun.
            InvalidateArrange();
        }
        else if (change.Property == LeadingContentProperty)
        {
            PseudoClasses.Set(":has-leading", change.NewValue is not null);
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _shell = e.NameScope.Find<Border>("PART_Shell");
        _label = e.NameScope.Find<TextBlock>("PART_Label");
        _fittedSlot = null;
    }

    // The open waits for the short come-out animation; a click during it is part of the same open.
    protected override void OnClick()
    {
        if (!OpenTransition || _shell is null)
        {
            base.OnClick();
            return;
        }

        if (_opening.IsCompleted)
        {
            _opening = ComeOutThenOpenAsync(_shell);
        }
    }

    private async Task ComeOutThenOpenAsync(Border shell)
    {
        try
        {
            await ComeOut.RunAsync(shell);
        }
        finally
        {
            base.OnClick();
        }
    }

    // A double-click's second press must not open the tape again, and modifier clicks are selection
    // gestures for the host list; leaving them unhandled lets them bubble there.
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.ClickCount > 1 || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        base.OnPointerPressed(e);
    }

    // The face is laid out in design units and scaled by the Viewbox, so the on-screen floor and step are divided by the
    // scale. The hover and open transitions are render transforms and never reach this.
    private void FitLabel(double scale)
    {
        if (_label?.Parent is not Control slot || scale <= 0
            || !this.TryFindResource("Vcr.FontSize.Hand", ActualThemeVariant, out var resource) || resource is not double maxSize)
        {
            return;
        }

        var width = slot.Bounds.Width - _label.Margin.Left - _label.Margin.Right;
        var height = slot.Bounds.Height - _label.Margin.Top - _label.Margin.Bottom;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var key = new LabelSlot(Label ?? "", width, height, maxSize, MinLabelFontSize / scale, 1 / scale);
        if (key == _fittedSlot)
        {
            return;
        }

        _fittedSlot = key;
        if (!LabelFits.TryGetValue(key, out var fit))
        {
            var label = _label;
            var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
            fit = LabelFitting.Fit(width, height, key.MaxSize, key.MinSize, key.Step, size => MeasureLabel(key.Text, typeface, size, width, label));
            if (LabelFits.Count >= MaxLabelFits)
            {
                LabelFits.Clear();
            }

            LabelFits[key] = fit;
        }

        _label.FontSize = fit.FontSize;
        _label.MaxLines = fit.MaxLines;
        _label.TextWrapping = fit.MaxLines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    private static TextMeasure MeasureLabel(string text, Typeface typeface, double fontSize, double width, TextBlock label)
    {
        using var layout = new TextLayout(text, typeface, fontSize, null, TextAlignment.Left, TextWrapping.Wrap, TextTrimming.None, null,
            FlowDirection.LeftToRight, width, double.PositiveInfinity, label.LineHeight, label.LetterSpacing);
        return new TextMeasure(layout.TextLines.Count, layout.Width, layout.Height);
    }

    private readonly record struct LabelSlot(string Text, double Width, double Height, double MaxSize, double MinSize, double Step);

    private void UpdateStickerClasses()
    {
        PseudoClasses.Set(":branded", Sticker == SpineSticker.Branded);
        PseudoClasses.Set(":graph", Sticker == SpineSticker.Graph);
        PseudoClasses.Set(":cream", Sticker == SpineSticker.Cream);
    }
}
