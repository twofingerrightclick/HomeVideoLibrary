using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.UI.Controls;

public sealed class TagEventArgs(RoutedEvent routedEvent, string tag) : RoutedEventArgs(routedEvent)
{
    public string Tag { get; } = tag;
}

/// <summary>
/// Free-form tag entry that renders chosen tags as removable chips and suggests library tags as you type.
/// </summary>
/// <remarks>
/// <para><see cref="Tags"/> is edited in place when the list is writable; a read-only list (such as an
/// array) is replaced with an edited <see cref="ObservableCollection{T}"/> copy. Tags are unique ignoring
/// case, and a typed tag that matches a library tag takes the library's casing.</para>
/// <para>Keys in the entry: Enter commits the highlighted suggestion, else the typed text. Comma commits
/// the text before it; pasted comma-separated text commits every piece. Down opens the suggestions
/// when the entry is empty. Up/Down move the highlight and wrap at either end. Tab accepts the
/// highlighted suggestion while the list is open, otherwise moves focus. Esc closes the list and keeps
/// the text. Backspace in an empty entry removes the last chip.</para>
/// </remarks>
[TemplatePart(FieldPartName, typeof(Border))]
[TemplatePart(ChipPanelPartName, typeof(Panel))]
[TemplatePart(EntryPartName, typeof(TextBox))]
[TemplatePart(SuggestionPanelPartName, typeof(Panel))]
[TemplatePart(DropDownPartName, typeof(Control))]
public class TagChipInput : TemplatedControl
{
    public const string FieldPartName = "PART_Field";
    public const string ChipPanelPartName = "PART_ChipPanel";
    public const string EntryPartName = "PART_Entry";
    public const string SuggestionPanelPartName = "PART_Suggestions";
    public const string DropDownPartName = "PART_DropDown";

    public static readonly StyledProperty<IList<string>?> TagsProperty =
        AvaloniaProperty.Register<TagChipInput, IList<string>?>(nameof(Tags), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable<TagFrequency>?> SuggestionsSourceProperty =
        AvaloniaProperty.Register<TagChipInput, IEnumerable<TagFrequency>?>(nameof(SuggestionsSource));

    public static readonly StyledProperty<int> MaxSuggestionsProperty =
        AvaloniaProperty.Register<TagChipInput, int>(nameof(MaxSuggestions), 8, validate: value => value >= 0);

    public static readonly StyledProperty<string?> WatermarkProperty =
        TextBox.WatermarkProperty.AddOwner<TagChipInput>();

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagChipInput, string?>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);

    public static readonly DirectProperty<TagChipInput, bool> IsDropDownOpenProperty =
        AvaloniaProperty.RegisterDirect<TagChipInput, bool>(nameof(IsDropDownOpen), o => o.IsDropDownOpen);

    public static readonly RoutedEvent<TagEventArgs> TagAddedEvent =
        RoutedEvent.Register<TagChipInput, TagEventArgs>(nameof(TagAdded), RoutingStrategies.Bubble);

    public static readonly RoutedEvent<TagEventArgs> TagRemovedEvent =
        RoutedEvent.Register<TagChipInput, TagEventArgs>(nameof(TagRemoved), RoutingStrategies.Bubble);

    private Border? _field;
    private Panel? _chipPanel;
    private TextBox? _entry;
    private Panel? _suggestionPanel;
    private Control? _dropDown;
    private INotifyCollectionChanged? _observedTags;
    private INotifyCollectionChanged? _observedSource;
    private IReadOnlyList<TagFrequency> _suggestions = [];
    private int _highlightedIndex = -1;
    private bool _isDropDownOpen;

    // Down on an empty entry asks for the full list; Esc hides the list until the text changes.
    private bool _isBrowsing;
    private bool _isDismissed;

    public TagChipInput()
    {
        SetCurrentValue(TagsProperty, new ObservableCollection<string>());
    }

    public IList<string>? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    /// <summary>Library-wide tag usage. Re-ranked live when it raises collection changes.</summary>
    public IEnumerable<TagFrequency>? SuggestionsSource
    {
        get => GetValue(SuggestionsSourceProperty);
        set => SetValue(SuggestionsSourceProperty, value);
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

    /// <summary>Uncommitted text in the entry.</summary>
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

    /// <summary>Ranked suggestions currently offered; empty while the list is closed.</summary>
    public IReadOnlyList<TagFrequency> Suggestions => _suggestions;

    /// <summary>Index into <see cref="Suggestions"/> of the keyboard highlight, or -1.</summary>
    public int HighlightedIndex => _highlightedIndex;

    public event EventHandler<TagEventArgs>? TagAdded
    {
        add => AddHandler(TagAddedEvent, value);
        remove => RemoveHandler(TagAddedEvent, value);
    }

    public event EventHandler<TagEventArgs>? TagRemoved
    {
        add => AddHandler(TagRemovedEvent, value);
        remove => RemoveHandler(TagRemovedEvent, value);
    }

    /// <summary>Normalizes and adds each comma-separated tag, skipping case-insensitive duplicates.</summary>
    public void AddTags(string? entry)
    {
        foreach (var tag in TagRules.SplitEntry(entry))
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
            _entry.RemoveHandler(TextInputEvent, OnEntryTextInput);
            _entry.RemoveHandler(TextBox.PastingFromClipboardEvent, OnEntryPasting);
            _entry.GotFocus -= OnEntryFocusChanged;
            _entry.LostFocus -= OnEntryFocusChanged;
        }

        _field?.RemoveHandler(PointerPressedEvent, OnFieldPointerPressed);

        _field = e.NameScope.Find<Border>(FieldPartName);
        _chipPanel = e.NameScope.Find<Panel>(ChipPanelPartName);
        _entry = e.NameScope.Find<TextBox>(EntryPartName);
        _suggestionPanel = e.NameScope.Find<Panel>(SuggestionPanelPartName);
        _dropDown = e.NameScope.Find<Control>(DropDownPartName);

        if (_entry is not null)
        {
            // Tunnel so these run before TextBox consumes Up/Down/Enter/Backspace and inserts commas.
            _entry.AddHandler(KeyDownEvent, OnEntryKeyDown, RoutingStrategies.Tunnel);
            _entry.AddHandler(TextInputEvent, OnEntryTextInput, RoutingStrategies.Tunnel);
            _entry.AddHandler(TextBox.PastingFromClipboardEvent, OnEntryPasting);
            _entry.GotFocus += OnEntryFocusChanged;
            _entry.LostFocus += OnEntryFocusChanged;
        }

        _field?.AddHandler(PointerPressedEvent, OnFieldPointerPressed);

        RebuildChips();
        RefreshSuggestions();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TagsProperty)
        {
            ObserveTags(change.GetNewValue<IList<string>?>());
        }
        else if (change.Property == SuggestionsSourceProperty)
        {
            ObserveSource(change.GetNewValue<IEnumerable<TagFrequency>?>());
        }
        else if (change.Property == TextProperty)
        {
            _isDismissed = false;
            _highlightedIndex = -1;
            RefreshSuggestions();
        }
        else if (change.Property == MaxSuggestionsProperty || change.Property == IsEffectivelyEnabledProperty)
        {
            RefreshSuggestions();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CloseDropDown();
    }

    private void ObserveTags(IList<string>? tags)
    {
        if (_observedTags is not null)
        {
            _observedTags.CollectionChanged -= OnTagsCollectionChanged;
            _observedTags = null;
        }

        if (tags is null)
        {
            SetCurrentValue(TagsProperty, new ObservableCollection<string>());
            return;
        }

        if (tags is INotifyCollectionChanged observable)
        {
            _observedTags = observable;
            observable.CollectionChanged += OnTagsCollectionChanged;
        }

        RebuildChips();
        RefreshSuggestions();
    }

    private void ObserveSource(IEnumerable<TagFrequency>? source)
    {
        if (_observedSource is not null)
        {
            _observedSource.CollectionChanged -= OnSourceCollectionChanged;
        }

        _observedSource = source as INotifyCollectionChanged;
        if (_observedSource is not null)
        {
            _observedSource.CollectionChanged += OnSourceCollectionChanged;
        }

        RefreshSuggestions();
    }

    private void OnTagsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildChips();
        RefreshSuggestions();
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSuggestions();

    private void OnEntryFocusChanged(object? sender, RoutedEventArgs e)
    {
        if (_entry?.IsFocused != true)
        {
            _isBrowsing = false;
            _isDismissed = false;
            _highlightedIndex = -1;
        }

        RefreshSuggestions();
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
                    CommitSuggestion(highlighted);
                    e.Handled = true;
                }
                else if (TagRules.Normalize(Text) is not null)
                {
                    var text = Text;
                    ClearEntry();
                    AddTags(text);
                    e.Handled = true;
                }

                break;
            case Key.Tab when plain && HighlightedSuggestion is { } tabbed:
                CommitSuggestion(tabbed);
                e.Handled = true;
                break;
            case Key.Escape when IsDropDownOpen:
                _isDismissed = true;
                _highlightedIndex = -1;
                RefreshSuggestions();
                e.Handled = true;
                break;
            case Key.Back when string.IsNullOrEmpty(Text) && Tags is { Count: > 0 } tags:
                RemoveTagAt(tags.Count - 1);
                e.Handled = true;
                break;
        }
    }

    private void OnEntryTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text?.Contains(',') == true)
        {
            e.Handled = true;
            InsertIntoEntry(e.Text, commitTrailing: false);
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
        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text))
        {
            InsertIntoEntry(text, commitTrailing: text.Contains(','));
        }
    }

    /// <summary>
    /// Replaces the entry selection with <paramref name="inserted"/>. Text up to the last comma is
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

        var lastComma = composed.LastIndexOf(',');
        if (lastComma < 0 && !commitTrailing)
        {
            SetEntryText(composed, start + inserted.Length);
            return;
        }

        var committed = commitTrailing ? composed : composed[..lastComma];
        var remainder = commitTrailing ? string.Empty : composed[(lastComma + 1)..].TrimStart();
        SetEntryText(remainder, remainder.Length);
        AddTags(committed);
    }

    private void SetEntryText(string text, int caretIndex)
    {
        SetCurrentValue(TextProperty, text);
        if (_entry is not null)
        {
            _entry.ClearSelection();
            _entry.CaretIndex = caretIndex;
        }
    }

    private void ClearEntry()
    {
        _isBrowsing = false;
        SetEntryText(string.Empty, 0);
    }

    private void CommitSuggestion(TagFrequency suggestion)
    {
        ClearEntry();
        AddTag(suggestion.Tag);
        _entry?.Focus();
    }

    private void AddTag(string tag)
    {
        var tags = Tags ?? [];
        var existing = IndexOfTag(tags, tag);
        if (existing >= 0)
        {
            ChipAt(existing)?.Flash();
            return;
        }

        var canonical = SuggestionsSource is { } source
            ? TagRules.MergeCase(source).FirstOrDefault(f => TagRules.Comparer.Equals(f.Tag, tag))?.Tag ?? tag
            : tag;
        EditTags(list => list.Add(canonical));
        RaiseEvent(new TagEventArgs(TagAddedEvent, canonical));
    }

    private void RemoveTagAt(int index)
    {
        var tag = Tags![index];
        EditTags(list => list.RemoveAt(index));
        RaiseEvent(new TagEventArgs(TagRemovedEvent, tag));
        _entry?.Focus();
    }

    private void EditTags(Action<IList<string>> edit)
    {
        var tags = Tags ?? [];
        if (tags.IsReadOnly)
        {
            var copy = new ObservableCollection<string>(tags);
            edit(copy);
            SetCurrentValue(TagsProperty, copy);
            return;
        }

        edit(tags);
        if (tags is not INotifyCollectionChanged)
        {
            RebuildChips();
            RefreshSuggestions();
        }
    }

    private static int IndexOfTag(IList<string> tags, string tag)
    {
        for (var i = 0; i < tags.Count; i++)
        {
            if (TagRules.Comparer.Equals(tags[i], tag))
            {
                return i;
            }
        }

        return -1;
    }

    private TagChip? ChipAt(int index) => _chipPanel?.Children.OfType<TagChip>().ElementAtOrDefault(index);

    private void RebuildChips()
    {
        if (_chipPanel is null)
        {
            return;
        }

        foreach (var chip in _chipPanel.Children.OfType<TagChip>().ToList())
        {
            chip.RemoveRequested -= OnChipRemoveRequested;
            _chipPanel.Children.Remove(chip);
        }

        var index = 0;
        foreach (var tag in Tags ?? [])
        {
            var chip = new TagChip { Text = tag };
            chip.RemoveRequested += OnChipRemoveRequested;
            _chipPanel.Children.Insert(index++, chip);
        }
    }

    private void OnChipRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is TagChip chip && _chipPanel is not null)
        {
            var index = _chipPanel.Children.OfType<TagChip>().ToList().IndexOf(chip);
            if (index >= 0)
            {
                RemoveTagAt(index);
            }
        }
    }

    private TagFrequency? HighlightedSuggestion =>
        IsDropDownOpen && _highlightedIndex >= 0 && _highlightedIndex < _suggestions.Count ? _suggestions[_highlightedIndex] : null;

    private bool MoveHighlight(int step)
    {
        if (!IsDropDownOpen)
        {
            if (step < 0)
            {
                return false;
            }

            _isBrowsing = true;
            _isDismissed = false;
            RefreshSuggestions();
            if (!IsDropDownOpen)
            {
                return false;
            }
        }

        var count = _suggestions.Count;
        _highlightedIndex = _highlightedIndex < 0
            ? (step > 0 ? 0 : count - 1)
            : (_highlightedIndex + step + count) % count;
        UpdateHighlight();
        return true;
    }

    private void UpdateHighlight()
    {
        if (_suggestionPanel is null)
        {
            return;
        }

        for (var i = 0; i < _suggestionPanel.Children.Count; i++)
        {
            if (_suggestionPanel.Children[i] is TagSuggestionItem item)
            {
                item.IsHighlighted = i == _highlightedIndex;
                if (item.IsHighlighted)
                {
                    item.BringIntoView();
                }
            }
        }
    }

    private void RefreshSuggestions()
    {
        var query = TagRules.Normalize(Text);
        var wanted = _entry is { IsFocused: true }
            && IsEffectivelyEnabled
            && !_isDismissed
            && (_isBrowsing || query is not null);
        _suggestions = wanted && SuggestionsSource is { } source
            ? TagRules.RankSuggestions(query, source, Tags ?? [], MaxSuggestions)
            : [];
        if (_highlightedIndex >= _suggestions.Count)
        {
            _highlightedIndex = -1;
        }

        RebuildSuggestionRows(query ?? string.Empty);
        if (_dropDown is not null && _field is not null)
        {
            _dropDown.Width = _field.Bounds.Width;
        }

        IsDropDownOpen = _suggestions.Count > 0;
    }

    private void CloseDropDown()
    {
        _suggestions = [];
        _highlightedIndex = -1;
        _suggestionPanel?.Children.Clear();
        IsDropDownOpen = false;
    }

    private void RebuildSuggestionRows(string query)
    {
        if (_suggestionPanel is null)
        {
            return;
        }

        _suggestionPanel.Children.Clear();
        for (var i = 0; i < _suggestions.Count; i++)
        {
            var suggestion = _suggestions[i];
            var match = TagRules.FindMatch(suggestion.Tag, query);
            var item = new TagSuggestionItem
            {
                Text = suggestion.Tag,
                Count = suggestion.Count,
                MatchStart = match.Start,
                MatchLength = match.Length,
                IsHighlighted = i == _highlightedIndex,
            };
            item.PointerPressed += OnSuggestionPointerPressed;
            _suggestionPanel.Children.Add(item);
        }
    }

    private void OnSuggestionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TagSuggestionItem { Text: { } tag } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Handled = true;
            CommitSuggestion(new TagFrequency(tag, 0));
        }
    }
}

/// <summary>A committed tag drawn as a raised plastic pill with a remove button.</summary>
[TemplatePart(RemovePartName, typeof(Button))]
[PseudoClasses(DuplicatePseudoClass)]
public class TagChip : TemplatedControl
{
    public const string RemovePartName = "PART_Remove";
    private const string DuplicatePseudoClass = ":duplicate";
    private static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(450);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagChip, string?>(nameof(Text));

    private Button? _removeButton;
    private IDisposable? _flashTimer;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public event EventHandler? RemoveRequested;

    /// <summary>Briefly sets <c>:duplicate</c> to point at this chip after a repeated entry.</summary>
    public void Flash()
    {
        _flashTimer?.Dispose();
        PseudoClasses.Set(DuplicatePseudoClass, true);
        _flashTimer = DispatcherTimer.RunOnce(() => PseudoClasses.Set(DuplicatePseudoClass, false), FlashDuration);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_removeButton is not null)
        {
            _removeButton.Click -= OnRemoveClick;
        }

        _removeButton = e.NameScope.Find<Button>(RemovePartName);
        if (_removeButton is not null)
        {
            _removeButton.Click += OnRemoveClick;
        }
    }

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RemoveRequested?.Invoke(this, EventArgs.Empty);
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
