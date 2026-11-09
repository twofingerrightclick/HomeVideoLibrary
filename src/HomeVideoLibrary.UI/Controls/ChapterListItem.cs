using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.UI.Controls;

public sealed class ChapterTitleCommittedEventArgs(RoutedEvent routedEvent, string oldTitle, string newTitle)
    : RoutedEventArgs(routedEvent)
{
    public string OldTitle { get; } = oldTitle;

    public string NewTitle { get; } = newTitle;
}

/// <summary>
/// One row of the Chapters inspector: start time, title, and trailing host actions in
/// <see cref="ContentControl.Content"/>. Clicking the row raises <see cref="JumpRequested"/>.
/// Double-click or F2 edits the title inline; Enter or losing focus commits (raising
/// <see cref="TitleCommitted"/>), Escape restores the previous title. With <see cref="IsPlaceholder"/>
/// the row is the yellow "a chapter added now would go here" marker and is not editable or jumpable.
/// </summary>
[TemplatePart("PART_TitleBox", typeof(TextBox))]
[PseudoClasses(":placeholder", ":current", ":editing")]
public class ChapterListItem : ContentControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ChapterListItem, string>(nameof(Title), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<TimeSpan> StartProperty =
        AvaloniaProperty.Register<ChapterListItem, TimeSpan>(nameof(Start));

    public static readonly DirectProperty<ChapterListItem, string> StartTextProperty =
        AvaloniaProperty.RegisterDirect<ChapterListItem, string>(nameof(StartText), o => o.StartText);

    /// <summary><see cref="StartText"/> with every digit lit, drawn faintly behind it as unlit segments.</summary>
    public static readonly DirectProperty<ChapterListItem, string> StartGhostTextProperty =
        AvaloniaProperty.RegisterDirect<ChapterListItem, string>(nameof(StartGhostText), o => o.StartGhostText);

    public static readonly StyledProperty<bool> IsPlaceholderProperty =
        AvaloniaProperty.Register<ChapterListItem, bool>(nameof(IsPlaceholder));

    /// <summary>The chapter containing the playhead.</summary>
    public static readonly StyledProperty<bool> IsCurrentProperty =
        AvaloniaProperty.Register<ChapterListItem, bool>(nameof(IsCurrent));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<ChapterListItem, bool>(nameof(IsEditing), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Text shown in place of an empty title (and as the placeholder row's caption).</summary>
    public static readonly StyledProperty<string?> TitleWatermarkProperty =
        AvaloniaProperty.Register<ChapterListItem, string?>(nameof(TitleWatermark), "Untitled chapter");

    public static readonly RoutedEvent<RoutedEventArgs> JumpRequestedEvent =
        RoutedEvent.Register<ChapterListItem, RoutedEventArgs>(nameof(JumpRequested), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<ChapterTitleCommittedEventArgs> TitleCommittedEvent =
        RoutedEvent.Register<ChapterListItem, ChapterTitleCommittedEventArgs>(nameof(TitleCommitted), RoutingStrategies.Bubble);

    private TextBox? _titleBox;
    private string _startText = ScrubTimeline.FormatTimestamp(TimeSpan.Zero);
    private string _startGhostText = "8:88";
    private bool _finishingEdit;

    static ChapterListItem()
    {
        FocusableProperty.OverrideDefaultValue<ChapterListItem>(true);
    }

    public ChapterListItem()
    {
        DoubleTapped += OnDoubleTapped;
    }

    public event EventHandler<RoutedEventArgs>? JumpRequested
    {
        add => AddHandler(JumpRequestedEvent, value);
        remove => RemoveHandler(JumpRequestedEvent, value);
    }

    public event EventHandler<ChapterTitleCommittedEventArgs>? TitleCommitted
    {
        add => AddHandler(TitleCommittedEvent, value);
        remove => RemoveHandler(TitleCommittedEvent, value);
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public TimeSpan Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public string StartText
    {
        get => _startText;
        private set => SetAndRaise(StartTextProperty, ref _startText, value);
    }

    public string StartGhostText
    {
        get => _startGhostText;
        private set => SetAndRaise(StartGhostTextProperty, ref _startGhostText, value);
    }

    public bool IsPlaceholder
    {
        get => GetValue(IsPlaceholderProperty);
        set => SetValue(IsPlaceholderProperty, value);
    }

    public bool IsCurrent
    {
        get => GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    public bool IsEditing
    {
        get => GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public string? TitleWatermark
    {
        get => GetValue(TitleWatermarkProperty);
        set => SetValue(TitleWatermarkProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_titleBox is not null)
        {
            _titleBox.KeyDown -= OnTitleBoxKeyDown;
            _titleBox.LostFocus -= OnTitleBoxLostFocus;
        }

        _titleBox = e.NameScope.Find<TextBox>("PART_TitleBox");
        if (_titleBox is not null)
        {
            _titleBox.KeyDown += OnTitleBoxKeyDown;
            _titleBox.LostFocus += OnTitleBoxLostFocus;
            if (IsEditing)
            {
                BeginEditVisuals();
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StartProperty)
        {
            StartText = ScrubTimeline.FormatTimestamp(change.GetNewValue<TimeSpan>());
            StartGhostText = new string(StartText.Select(ch => char.IsDigit(ch) ? '8' : ch).ToArray());
        }
        else if (change.Property == IsPlaceholderProperty)
        {
            var placeholder = change.GetNewValue<bool>();
            PseudoClasses.Set(":placeholder", placeholder);
            if (placeholder)
            {
                IsEditing = false;
            }
        }
        else if (change.Property == IsCurrentProperty)
        {
            PseudoClasses.Set(":current", change.GetNewValue<bool>());
        }
        else if (change.Property == IsEditingProperty)
        {
            var editing = change.GetNewValue<bool>() && !IsPlaceholder;
            PseudoClasses.Set(":editing", editing);
            if (editing)
            {
                BeginEditVisuals();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!e.Handled && !IsEditing && !IsPlaceholder && e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(JumpRequestedEvent));
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!IsPlaceholder)
        {
            e.Handled = true;
            IsEditing = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsEditing || IsPlaceholder)
        {
            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            IsEditing = true;
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            RaiseEvent(new RoutedEventArgs(JumpRequestedEvent));
        }
    }

    private void BeginEditVisuals()
    {
        if (_titleBox is null)
        {
            return;
        }

        _titleBox.Text = Title;

        // The box only becomes visible after the :editing style applies, so focus on the next pass.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEditing && _titleBox is not null)
            {
                _titleBox.Focus();
                _titleBox.SelectAll();
            }
        });
    }

    private void OnTitleBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FinishEdit(commit: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FinishEdit(commit: false);
        }
    }

    private void OnTitleBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (IsEditing)
        {
            FinishEdit(commit: true);
        }
    }

    private void FinishEdit(bool commit)
    {
        if (_finishingEdit || _titleBox is null)
        {
            return;
        }

        _finishingEdit = true;
        try
        {
            var oldTitle = Title;
            var newTitle = (_titleBox.Text ?? string.Empty).Trim();
            IsEditing = false;
            Focus();
            if (commit && newTitle != oldTitle)
            {
                Title = newTitle;
                RaiseEvent(new ChapterTitleCommittedEventArgs(TitleCommittedEvent, oldTitle, newTitle));
            }
        }
        finally
        {
            _finishingEdit = false;
        }
    }
}
