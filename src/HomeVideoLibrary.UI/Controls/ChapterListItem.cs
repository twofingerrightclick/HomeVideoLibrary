using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>
/// One chapter row of the Chapters inspector: start time, title, and trailing host actions in
/// <see cref="ContentControl.Content"/>. Clicking the row executes <see cref="JumpCommand"/>. Double-click
/// or F2 sets <see cref="IsEditing"/>; while editing, a title field is created for this edit only (so its
/// undo history never reaches another chapter) and bound to <see cref="DraftTitle"/>. Enter or leaving
/// the field executes <see cref="CommitTitleCommand"/> with the trimmed draft when it differs from
/// <see cref="Title"/>; Escape discards the draft. Bind <see cref="IsEditing"/> and
/// <see cref="DraftTitle"/> two-way to the row's view model so an edit survives playhead updates.
/// </summary>
[TemplatePart(EditorHostPartName, typeof(ContentControl))]
[TemplatePart(ActionsPartName, typeof(Control))]
[PseudoClasses(":current", ":editing")]
public class ChapterListItem : ContentControl
{
    public const string EditorHostPartName = "PART_Editor";
    public const string ActionsPartName = "PART_ContentPresenter";

    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ChapterListItem, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<TimeSpan> StartProperty =
        AvaloniaProperty.Register<ChapterListItem, TimeSpan>(nameof(Start));

    public static readonly DirectProperty<ChapterListItem, string> StartTextProperty =
        AvaloniaProperty.RegisterDirect<ChapterListItem, string>(nameof(StartText), o => o.StartText);

    /// <summary><see cref="StartText"/> with every digit lit, drawn faintly behind it as unlit segments.</summary>
    public static readonly DirectProperty<ChapterListItem, string> StartGhostTextProperty =
        AvaloniaProperty.RegisterDirect<ChapterListItem, string>(nameof(StartGhostText), o => o.StartGhostText);

    /// <summary>The chapter containing the playhead.</summary>
    public static readonly StyledProperty<bool> IsCurrentProperty =
        AvaloniaProperty.Register<ChapterListItem, bool>(nameof(IsCurrent));

    public static readonly StyledProperty<bool> IsEditingProperty =
        AvaloniaProperty.Register<ChapterListItem, bool>(nameof(IsEditing), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>The title being typed; null when not editing. Seeded from <see cref="Title"/> when editing starts.</summary>
    public static readonly StyledProperty<string?> DraftTitleProperty =
        AvaloniaProperty.Register<ChapterListItem, string?>(nameof(DraftTitle), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Text shown in place of an empty title.</summary>
    public static readonly StyledProperty<string?> TitleWatermarkProperty =
        AvaloniaProperty.Register<ChapterListItem, string?>(nameof(TitleWatermark), "Untitled chapter");

    public static readonly StyledProperty<ICommand?> JumpCommandProperty =
        AvaloniaProperty.Register<ChapterListItem, ICommand?>(nameof(JumpCommand));

    /// <summary>Executed with the new title string.</summary>
    public static readonly StyledProperty<ICommand?> CommitTitleCommandProperty =
        AvaloniaProperty.Register<ChapterListItem, ICommand?>(nameof(CommitTitleCommand));

    private ContentControl? _editorHost;
    private Control? _actions;
    private TextBox? _editor;
    private string _startText = ScrubTimeline.FormatTimestamp(TimeSpan.Zero);
    private string _startGhostText = "8:88";
    private bool _pressedHere;

    static ChapterListItem()
    {
        FocusableProperty.OverrideDefaultValue<ChapterListItem>(true);
    }

    public ChapterListItem()
    {
        DoubleTapped += OnDoubleTapped;
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

    public string? DraftTitle
    {
        get => GetValue(DraftTitleProperty);
        set => SetValue(DraftTitleProperty, value);
    }

    public string? TitleWatermark
    {
        get => GetValue(TitleWatermarkProperty);
        set => SetValue(TitleWatermarkProperty, value);
    }

    public ICommand? JumpCommand
    {
        get => GetValue(JumpCommandProperty);
        set => SetValue(JumpCommandProperty, value);
    }

    public ICommand? CommitTitleCommand
    {
        get => GetValue(CommitTitleCommandProperty);
        set => SetValue(CommitTitleCommandProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        CloseEditor();
        _editorHost = e.NameScope.Find<ContentControl>(EditorHostPartName);
        _actions = e.NameScope.Find<Control>(ActionsPartName);
        if (IsEditing)
        {
            OpenEditor();
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
        else if (change.Property == IsCurrentProperty)
        {
            PseudoClasses.Set(":current", change.GetNewValue<bool>());
        }
        else if (change.Property == IsEditingProperty)
        {
            // Unhook the field before :editing hides it: hiding a focused field raises its LostFocus.
            var editing = change.GetNewValue<bool>();
            if (editing)
            {
                OpenEditor();
            }
            else
            {
                CloseEditor();
            }

            PseudoClasses.Set(":editing", editing);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressedHere = e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pressedHere = _pressedHere;
        _pressedHere = false;
        if (pressedHere && !e.Handled && !IsEditing && e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Handled = true;
            Execute(JumpCommand, null);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || IsEditing || !ReferenceEquals(e.Source, this))
        {
            return;
        }

        if (e.Key == Key.F2)
        {
            e.Handled = true;
            SetCurrentValue(IsEditingProperty, true);
        }
        else if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            Execute(JumpCommand, null);
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Double-clicks on the host's trailing actions (e.g. a delete key) are not rename gestures.
        if (e.Source is Visual source && _actions is not null && (source == _actions || _actions.IsVisualAncestorOf(source)))
        {
            return;
        }

        e.Handled = true;
        SetCurrentValue(IsEditingProperty, true);
    }

    private void OpenEditor()
    {
        if (_editorHost is null || _editor is not null)
        {
            return;
        }

        if (DraftTitle is null)
        {
            SetCurrentValue(DraftTitleProperty, Title);
        }

        var editor = new TextBox
        {
            MinHeight = 28,
            Padding = new Thickness(8, 3),
            Watermark = TitleWatermark,
            [!TextBox.TextProperty] = this[!!DraftTitleProperty],
        };
        editor.KeyDown += OnEditorKeyDown;
        editor.LostFocus += OnEditorLostFocus;
        editor.Loaded += OnEditorLoaded;
        _editor = editor;
        _editorHost.Content = editor;
    }

    private static void OnEditorLoaded(object? sender, RoutedEventArgs e)
    {
        var editor = (TextBox)sender!;
        editor.Loaded -= OnEditorLoaded;
        editor.Focus();
        editor.SelectAll();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
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

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e) => FinishEdit(commit: true);

    private void FinishEdit(bool commit)
    {
        var draft = DraftTitle?.Trim() ?? string.Empty;

        SetCurrentValue(IsEditingProperty, false);
        SetCurrentValue(DraftTitleProperty, null);
        Focus();
        if (commit && draft != Title)
        {
            Execute(CommitTitleCommand, draft);
        }
    }

    private void CloseEditor()
    {
        if (_editor is null)
        {
            return;
        }

        _editor.KeyDown -= OnEditorKeyDown;
        _editor.LostFocus -= OnEditorLostFocus;
        _editor.Loaded -= OnEditorLoaded;
        _editor = null;
        if (_editorHost is not null)
        {
            _editorHost.Content = null;
        }
    }

    private static void Execute(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }
    }
}

/// <summary>
/// The yellow "a chapter added now goes here" row. Hosts keep one instance in the list and move it as the
/// playhead crosses chapter starts (see <see cref="ChapterPlacement.PlaceholderIndex"/>); it is never
/// turned into a chapter row.
/// </summary>
public class ChapterPlaceholderRow : TemplatedControl
{
    public static readonly StyledProperty<TimeSpan> StartProperty =
        AvaloniaProperty.Register<ChapterPlaceholderRow, TimeSpan>(nameof(Start));

    public static readonly DirectProperty<ChapterPlaceholderRow, string> StartTextProperty =
        AvaloniaProperty.RegisterDirect<ChapterPlaceholderRow, string>(nameof(StartText), o => o.StartText);

    public static readonly DirectProperty<ChapterPlaceholderRow, string> StartGhostTextProperty =
        AvaloniaProperty.RegisterDirect<ChapterPlaceholderRow, string>(nameof(StartGhostText), o => o.StartGhostText);

    private string _startText = ScrubTimeline.FormatTimestamp(TimeSpan.Zero);
    private string _startGhostText = "8:88";

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StartProperty)
        {
            StartText = ScrubTimeline.FormatTimestamp(change.GetNewValue<TimeSpan>());
            StartGhostText = new string(StartText.Select(ch => char.IsDigit(ch) ? '8' : ch).ToArray());
        }
    }
}
