using System.Collections.ObjectModel;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.Infrastructure;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Desktop.ViewModels.VideoPage;

/// <summary>The tags of a video or a chapter. This view model owns the list and the entry draft; the chip input only
/// requests changes through the commands, and tag identity is <see cref="TagRules"/>'.</summary>
public sealed class TagListViewModel : ViewModelBase
{
    private const int PopularCount = 12;
    private readonly Action _changed;
    private TagIndex _index = TagIndex.Empty;
    private string? _draft;

    /// <param name="changed">Called after the list changes, so the owner can recompute derived state.</param>
    public TagListViewModel(IEnumerable<string> tags, TagSuggestionProvider suggest, Action changed)
    {
        Items = new ObservableCollection<string>(tags);
        Suggest = suggest;
        _changed = changed;
        AddTagCommand = new RelayCommand<string>(Add);
        RemoveTagCommand = new RelayCommand<string>(Remove);
    }

    public ObservableCollection<string> Items { get; }

    /// <summary>The uncommitted entry text.</summary>
    public string? Draft
    {
        get => _draft;
        set { if (SetProperty(ref _draft, value)) _changed(); }
    }

    /// <summary>Whether tag text is typed but not added yet.</summary>
    public bool HasOpenDraft => TagRules.Split(Draft).Count > 0;

    /// <summary>Adds the typed tags, as leaving the entry would.</summary>
    public void CommitDraft()
    {
        foreach (var tag in TagRules.Split(Draft)) Add(tag);
        Draft = null;
    }

    public TagSuggestionProvider Suggest { get; }

    /// <summary>Parameter: the tag to add. A tag already in the list (by tag identity) is ignored.</summary>
    public RelayCommand<string> AddTagCommand { get; }

    /// <summary>Parameter: the tag to remove, matched by tag identity.</summary>
    public RelayCommand<string> RemoveTagCommand { get; }

    /// <summary>The library's most used tags not already in this list.</summary>
    public IReadOnlyList<string> Popular =>
        _index.All().Select(suggestion => suggestion.Tag).Where(tag => !TagRules.Contains(Items, tag)).Take(PopularCount).ToArray();

    /// <summary>Uses <paramref name="index"/> for <see cref="Popular"/>.</summary>
    public void ShowPopularFrom(TagIndex index)
    {
        if (ReferenceEquals(index, _index)) return;
        _index = index;
        OnPropertyChanged(nameof(Popular));
    }

    public bool Matches(IReadOnlyList<string> tags) => Items.SequenceEqual(tags, StringComparer.Ordinal);

    /// <summary>Makes the list equal <paramref name="tags"/>, touching only tags that differ.</summary>
    public void ApplyBaseline(IReadOnlyList<string> tags)
    {
        if (Matches(tags)) return;
        for (var index = Items.Count - 1; index >= 0; index--)
            if (!tags.Contains(Items[index], StringComparer.Ordinal)) Items.RemoveAt(index);
        for (var index = 0; index < tags.Count; index++)
        {
            var existing = Items.IndexOf(tags[index]);
            if (existing < 0) Items.Insert(index, tags[index]);
            else if (existing != index) Items.Move(existing, index);
        }
        Changed();
    }

    private void Add(string tag)
    {
        if (TagRules.Normalize(tag) is not { } normalized || TagRules.Contains(Items, normalized)) return;
        Items.Add(normalized);
        Changed();
    }

    private void Remove(string tag)
    {
        if (Items.FirstOrDefault(item => TagRules.Comparer.Equals(item, tag)) is not { } existing) return;
        Items.Remove(existing);
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Popular));
        _changed();
    }
}
