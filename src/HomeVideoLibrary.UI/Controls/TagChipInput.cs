using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HomeVideoLibrary.UI.Logic;
using CoreTagRules = HomeVideoLibrary.Core.TagRules;

namespace HomeVideoLibrary.UI.Controls;

/// <summary>One offered suggestion with the span of its tag that matched the query (-1 for none).</summary>
public sealed record TagSuggestionRow(string Tag, int Count, int MatchStart, int MatchLength);

/// <summary>
/// Tag entry that shows the video's tags as removable chips and suggests library tags as you type.
/// </summary>
/// <remarks>
/// <para>The control never edits <see cref="Tags"/>: it asks through <see cref="AddTagCommand"/> (with the
/// tag in its canonical spelling) and <see cref="RemoveTagCommand"/>, and shows whatever list the view model
/// then exposes. Tag identity and splitting come from <see cref="CoreTagRules"/>; an entry matching an
/// existing chip flashes that chip instead of adding. Bind <see cref="Text"/> two-way to the page view
/// model's draft so typing belongs to the video being edited; leaving the field commits it.</para>
/// <para>Keys in the entry: Enter commits the highlighted suggestion, else the typed text. A separator
/// (comma, CJK comma, line break) commits the text before it, and pasted lists commit every piece. Down
/// opens every suggestion when the entry is empty; Up/Down move the highlight and wrap. Tab accepts the
/// highlight, otherwise moves focus. Escape closes the list and keeps the text. A fresh, unmodified
/// Backspace in an empty entry removes the last chip.</para>
/// </remarks>
[TemplatePart(FieldPartName, typeof(Border))]
[TemplatePart(ChipsPartName, typeof(ItemsControl))]
[TemplatePart(EntryPartName, typeof(TextBox))]
[TemplatePart(SuggestionsPartName, typeof(ListBox))]
public class TagChipInput : TemplatedControl
{
    public const string FieldPartName = "PART_Field";
    public const string ChipsPartName = "PART_Chips";
    public const string EntryPartName = "PART_Entry";
    public const string SuggestionsPartName = "PART_Suggestions";

    public static readonly StyledProperty<IReadOnlyList<string>?> TagsProperty =
        AvaloniaProperty.Register<TagChipInput, IReadOnlyList<string>?>(nameof(Tags));

    /// <summary>Executed with the canonical tag string to add.</summary>
    public static readonly StyledProperty<ICommand?> AddTagCommandProperty =
        AvaloniaProperty.Register<TagChipInput, ICommand?>(nameof(AddTagCommand));

    /// <summary>Executed with the tag string to remove (chip X, or Backspace on an empty entry).</summary>
    public static readonly StyledProperty<ICommand?> RemoveTagCommandProperty =
        AvaloniaProperty.Register<TagChipInput, ICommand?>(nameof(RemoveTagCommand));

    public static readonly StyledProperty<TagSuggestionProvider?> SuggestionProviderProperty =
        AvaloniaProperty.Register<TagChipInput, TagSuggestionProvider?>(nameof(SuggestionProvider));

    public static readonly StyledProperty<int> MaxSuggestionsProperty =
        AvaloniaProperty.Register<TagChipInput, int>(nameof(MaxSuggestions), 8, validate: value => value >= 0);

    public static readonly StyledProperty<string?> WatermarkProperty =
        TextBox.WatermarkProperty.AddOwner<TagChipInput>();

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagChipInput, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly DirectProperty<TagChipInput, bool> IsDropDownOpenProperty =
        AvaloniaProperty.RegisterDirect<TagChipInput, bool>(nameof(IsDropDownOpen), o => o.IsDropDownOpen);

    public static readonly DirectProperty<TagChipInput, IReadOnlyList<TagSuggestionRow>> SuggestionsProperty =
        AvaloniaProperty.RegisterDirect<TagChipInput, IReadOnlyList<TagSuggestionRow>>(nameof(Suggestions), o => o.Suggestions);

    public static readonly DirectProperty<TagChipInput, TagSuggestionRow?> HighlightedSuggestionProperty =
        AvaloniaProperty.RegisterDirect<TagChipInput, TagSuggestionRow?>(nameof(HighlightedSuggestion), o => o.HighlightedSuggestion);

    private ItemsControl? _chips;
    private TextBox? _entry;
    private Border? _field;
    private ListBox? _suggestionList;
    private SuggestionState _state = SuggestionState.Closed;
    private IReadOnlyList<TagSuggestionRow> _suggestions = [];
    private TagSuggestionRow? _highlighted;
    private bool _isDropDownOpen;

    // Avalonia key events carry no repeat flag; KeyUp marks the end of a held Backspace.
    private bool _backspaceHeld;

    public IReadOnlyList<string>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    public ICommand? AddTagCommand
    {
        get => GetValue(AddTagCommandProperty);
        set => SetValue(AddTagCommandProperty, value);
    }

    public ICommand? RemoveTagCommand
    {
        get => GetValue(RemoveTagCommandProperty);
        set => SetValue(RemoveTagCommandProperty, value);
    }

    public TagSuggestionProvider? SuggestionProvider
    {
        get => GetValue(SuggestionProviderProperty);
        set => SetValue(SuggestionProviderProperty, value);
    }

    public int MaxSuggestions
    {
        get => GetValue(MaxSuggestionsProperty);
        set => SetValue(MaxSuggestionsProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>The uncommitted entry text (the draft).</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsDropDownOpen
    {
        get => _isDropDownOpen;
        private set => SetAndRaise(IsDropDownOpenProperty, ref _isDropDownOpen, value);
    }

    /// <summary>Suggestions currently offered, best first; empty while the list is closed.</summary>
    public IReadOnlyList<TagSuggestionRow> Suggestions
    {
        get => _suggestions;
        private set => SetAndRaise(SuggestionsProperty, ref _suggestions, value);
    }

    public TagSuggestionRow? HighlightedSuggestion
    {
        get => _highlighted;
        private set => SetAndRaise(HighlightedSuggestionProperty, ref _highlighted, value);
    }

    /// <summary>Commits every tag in <paramref name="entry"/> as if typed and confirmed.</summary>
    public void AddTags(string? entry)
    {
        foreach (var tag in CoreTagRules.Split(entry))
        {
            AddTag(tag);
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_entry is not null)
        {
            _entry.RemoveHandler(KeyDownEvent, OnEntryKeyDown);
            _entry.RemoveHandler(KeyUpEvent, OnEntryKeyUp);
            _entry.RemoveHandler(TextInputEvent, OnEntryTextInput);
            _entry.RemoveHandler(TextBox.PastingFromClipboardEvent, OnEntryPasting);
            _entry.GotFocus -= OnEntryGotFocus;
            _entry.LostFocus -= OnEntryLostFocus;
        }

        _field?.RemoveHandler(PointerPressedEvent, OnFieldPointerPressed);
        _suggestionList?.RemoveHandler(PointerPressedEvent, OnSuggestionPointerPressed);

        _field = e.NameScope.Find<Border>(FieldPartName);
        _chips = e.NameScope.Find<ItemsControl>(ChipsPartName);
        _entry = e.NameScope.Find<TextBox>(EntryPartName);
        _suggestionList = e.NameScope.Find<ListBox>(SuggestionsPartName);

        if (_entry is not null)
        {
            // Tunnel so these run before TextBox consumes Up/Down/Enter/Backspace and inserts separators.
            _entry.AddHandler(KeyDownEvent, OnEntryKeyDown, RoutingStrategies.Tunnel);
            _entry.AddHandler(KeyUpEvent, OnEntryKeyUp, RoutingStrategies.Tunnel);
            _entry.AddHandler(TextInputEvent, OnEntryTextInput, RoutingStrategies.Tunnel);
            _entry.AddHandler(TextBox.PastingFromClipboardEvent, OnEntryPasting);
            _entry.GotFocus += OnEntryGotFocus;
            _entry.LostFocus += OnEntryLostFocus;
        }

        _field?.AddHandler(PointerPressedEvent, OnFieldPointerPressed);

        // Tunnel: a click commits before the list could take focus from the entry or change selection.
        _suggestionList?.AddHandler(PointerPressedEvent, OnSuggestionPointerPressed, RoutingStrategies.Tunnel);
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _state = _state.TextChanged(CoreTagRules.Normalize(change.GetNewValue<string?>()));
            Refresh();
        }
        else if (change.Property == TagsProperty
            || change.Property == SuggestionProviderProperty
            || change.Property == MaxSuggestionsProperty
            || change.Property == IsEffectivelyEnabledProperty)
        {
            Refresh();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _state = SuggestionState.Closed;
        Refresh();
    }

    private void Refresh()
    {
        var query = CoreTagRules.Normalize(Text);
        var offered = _entry is { IsFocused: true } && IsEffectivelyEnabled && _state.WantsList(query) && SuggestionProvider is { } provider
            ? provider(query, Tags ?? [], MaxSuggestions)
            : [];
        _state = _state.Reconcile([.. offered.Select(f => f.Tag)]);
        Suggestions = [.. offered.Select(f => ToRow(f, query))];
        ShowHighlight();
        IsDropDownOpen = Suggestions.Count > 0;
    }

    private static TagSuggestionRow ToRow(TagFrequency suggestion, string? query)
    {
        var match = TagSuggestions.FindMatch(suggestion.Tag, query ?? string.Empty);
        return new TagSuggestionRow(suggestion.Tag, suggestion.Count, match.Start, match.Length);
    }

    private void ShowHighlight() =>
        HighlightedSuggestion = _suggestions.FirstOrDefault(r => _state.Highlighted is { } tag && CoreTagRules.Comparer.Equals(r.Tag, tag));

    private void OnEntryGotFocus(object? sender, GotFocusEventArgs e) => Refresh();

    // Leaving the field keeps what was typed as tags rather than silently holding it for later.
    private void OnEntryLostFocus(object? sender, RoutedEventArgs e)
    {
        _state = SuggestionState.Closed;
        if (CoreTagRules.Normalize(Text) is not null)
        {
            CommitText();
        }

        Refresh();
    }

    private void OnFieldPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _entry is null || !IsEffectivelyEnabled)
        {
            return;
        }

        _entry.Focus(NavigationMethod.Pointer);
        _entry.CaretIndex = _entry.Text?.Length ?? 0;
        e.Handled = true;
    }

    private void OnSuggestionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && (e.Source as StyledElement)?.DataContext is TagSuggestionRow row)
        {
            e.Handled = true;
            CommitTag(row.Tag);
        }
    }

    private void OnEntryKeyDown(object? sender, KeyEventArgs e)
    {
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            case Key.Down or Key.Up when plain:
                e.Handled = MoveHighlight(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.Enter when plain:
                if (HighlightedSuggestion is { } highlighted)
                {
                    CommitTag(highlighted.Tag);
                    e.Handled = true;
                }
                else if (CoreTagRules.Normalize(Text) is not null)
                {
                    CommitText();
                    e.Handled = true;
                }

                break;
            case Key.Tab when plain && HighlightedSuggestion is { } tabbed:
                CommitTag(tabbed.Tag);
                e.Handled = true;
                break;
            case Key.Escape when IsDropDownOpen:
                _state = _state.Dismiss();
                Refresh();
                e.Handled = true;
                break;
            case Key.Back:
                // Only a fresh, unmodified press on an already-empty entry removes a chip, so holding
                // Backspace to clear text (or Ctrl+Backspace) never eats chips once the text is gone.
                var removeChip = plain && !_backspaceHeld && string.IsNullOrEmpty(Text) && Tags is { Count: > 0 };
                _backspaceHeld = true;
                if (removeChip)
                {
                    Execute(RemoveTagCommand, Tags![^1]);
                    Refresh();
                    e.Handled = true;
                }

                break;
        }
    }

    private void OnEntryKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back)
        {
            _backspaceHeld = false;
        }
    }

    private bool MoveHighlight(int step)
    {
        if (!IsDropDownOpen)
        {
            if (step < 0)
            {
                return false;
            }

            _state = _state.Browse();
            Refresh();
            if (!IsDropDownOpen)
            {
                return false;
            }
        }

        _state = _state.Move([.. _suggestions.Select(r => r.Tag)], step);
        ShowHighlight();
        return true;
    }

    private void OnEntryTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { } text && LastSeparator(text) >= 0)
        {
            e.Handled = true;
            InsertIntoEntry(text, commitTrailing: false);
        }
    }

    private async void OnEntryPasting(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        e.Handled = true;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            return;
        }

        if (!string.IsNullOrEmpty(text))
        {
            InsertIntoEntry(text, commitTrailing: LastSeparator(text) >= 0);
        }
    }

    /// <summary>
    /// Replaces the entry selection with <paramref name="inserted"/>. Text up to the last separator is
    /// committed; what follows stays in the entry unless <paramref name="commitTrailing"/>.
    /// </summary>
    private void InsertIntoEntry(string inserted, bool commitTrailing)
    {
        var current = Text ?? string.Empty;
        var selectionStart = _entry?.SelectionStart ?? current.Length;
        var selectionEnd = _entry?.SelectionEnd ?? current.Length;
        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, current.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, current.Length);
        var composed = string.Concat(current.AsSpan(0, start), inserted, current.AsSpan(end));

        var lastSeparator = LastSeparator(composed);
        if (lastSeparator < 0 && !commitTrailing)
        {
            SetDraft(composed, start + inserted.Length);
            return;
        }

        var committed = commitTrailing ? composed : composed[..lastSeparator];
        var remainder = commitTrailing ? string.Empty : composed[(lastSeparator + 1)..].TrimStart();
        SetDraft(remainder, remainder.Length);
        AddTags(committed);
    }

    private static int LastSeparator(string text) => text.LastIndexOfAny([.. CoreTagRules.Separators]);

    private void SetDraft(string text, int caretIndex)
    {
        SetCurrentValue(TextProperty, text);
        if (_entry is not null)
        {
            _entry.ClearSelection();
            _entry.CaretIndex = caretIndex;
        }
    }

    private void CommitText()
    {
        var text = Text;
        SetDraft(string.Empty, 0);
        AddTags(text);
    }

    private void CommitTag(string tag)
    {
        SetDraft(string.Empty, 0);
        _state = SuggestionState.Closed;
        AddTag(tag);
        _entry?.Focus();
    }

    private void AddTag(string tag)
    {
        var tags = Tags ?? [];
        for (var i = 0; i < tags.Count; i++)
        {
            if (CoreTagRules.Comparer.Equals(tags[i], tag))
            {
                ChipAt(i)?.Flash();
                return;
            }
        }

        // The library's spelling wins over what was typed, so "grandma" joins the existing "Grandma".
        var canonical = SuggestionProvider?.Invoke(tag, [], Math.Max(1, MaxSuggestions))
            .FirstOrDefault(f => CoreTagRules.Comparer.Equals(f.Tag, tag))?.Tag ?? tag;
        Execute(AddTagCommand, canonical);
        Refresh();
    }

    private TagChip? ChipAt(int index) => (_chips?.ContainerFromIndex(index) as ContentPresenter)?.Child as TagChip;

    private static void Execute(ICommand? command, string tag)
    {
        if (command?.CanExecute(tag) == true)
        {
            command.Execute(tag);
        }
    }
}

/// <summary>A committed tag drawn as a raised plastic pill with a remove button.</summary>
[PseudoClasses(DuplicatePseudoClass)]
public class TagChip : TemplatedControl
{
    private const string DuplicatePseudoClass = ":duplicate";
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(450);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagChip, string?>(nameof(Text));

    /// <summary>Executed with <see cref="Text"/> when the chip's X is pressed.</summary>
    public static readonly StyledProperty<ICommand?> RemoveCommandProperty =
        AvaloniaProperty.Register<TagChip, ICommand?>(nameof(RemoveCommand));

    private IDisposable? _flashTimer;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public ICommand? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    /// <summary>Briefly sets <c>:duplicate</c> to point at this chip after a repeated entry.</summary>
    public void Flash()
    {
        _flashTimer?.Dispose();
        PseudoClasses.Set(DuplicatePseudoClass, true);
        _flashTimer = DispatcherTimer.RunOnce(() => PseudoClasses.Set(DuplicatePseudoClass, false), FlashDuration);
    }
}

/// <summary>One row of the suggestion list: tag text with the matched span emphasized, and its usage count.</summary>
[TemplatePart(TextPartName, typeof(TextBlock))]
[PseudoClasses(HighlightedPseudoClass)]
public class TagSuggestionItem : TemplatedControl
{
    public const string TextPartName = "PART_Text";
    private const string HighlightedPseudoClass = ":highlighted";

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagSuggestionItem, string?>(nameof(Text));

    public static readonly StyledProperty<int> CountProperty =
        AvaloniaProperty.Register<TagSuggestionItem, int>(nameof(Count));

    /// <summary>Start of the emphasized span in <see cref="Text"/>; -1 for none.</summary>
    public static readonly StyledProperty<int> MatchStartProperty =
        AvaloniaProperty.Register<TagSuggestionItem, int>(nameof(MatchStart), -1);

    public static readonly StyledProperty<int> MatchLengthProperty =
        AvaloniaProperty.Register<TagSuggestionItem, int>(nameof(MatchLength));

    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<TagSuggestionItem, bool>(nameof(IsHighlighted));

    private TextBlock? _text;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int Count
    {
        get => GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public int MatchStart
    {
        get => GetValue(MatchStartProperty);
        set => SetValue(MatchStartProperty, value);
    }

    public int MatchLength
    {
        get => GetValue(MatchLengthProperty);
        set => SetValue(MatchLengthProperty, value);
    }

    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _text = e.NameScope.Find<TextBlock>(TextPartName);
        UpdateText();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsHighlightedProperty)
        {
            PseudoClasses.Set(HighlightedPseudoClass, change.GetNewValue<bool>());
        }
        else if (change.Property == TextProperty || change.Property == MatchStartProperty || change.Property == MatchLengthProperty)
        {
            UpdateText();
        }
    }

    private void UpdateText()
    {
        if (_text is null)
        {
            return;
        }

        var text = Text ?? string.Empty;
        var start = MatchStart;
        var length = MatchLength;
        _text.Inlines?.Clear();
        if (start < 0 || length <= 0 || start + length > text.Length)
        {
            _text.Text = text;
            return;
        }

        _text.Inlines =
        [
            new Run(text[..start]),
            new Run(text.Substring(start, length)) { FontWeight = FontWeight.Bold },
            new Run(text[(start + length)..]),
        ];
    }
}
