using System.Collections.ObjectModel;
using System.Windows.Input;
using HomeVideoLibrary.Core;
using HomeVideoLibrary.UI.Logic;

namespace HomeVideoLibrary.Gallery.Demo;

/// <summary>
/// The tags of one video. The view model owns the list and the entry draft; the chip input only asks
/// for changes, so the draft dies with the video and never carries over to the next one.
/// </summary>
public sealed class TagEditorViewModel : ObservableObject
{
    private string? _draft;

    public TagEditorViewModel(IEnumerable<string> tags, TagSuggestionProvider suggest)
    {
        Tags = new ObservableCollection<string>(tags);
        Suggest = suggest;
        AddTag = new RelayCommand<string>(tag =>
        {
            if (!TagRules.Contains(Tags, tag))
            {
                Tags.Add(tag);
            }
        });
        RemoveTag = new RelayCommand<string>(tag =>
        {
            var existing = Tags.FirstOrDefault(t => TagRules.Comparer.Equals(t, tag));
            if (existing is not null)
            {
                Tags.Remove(existing);
            }
        });
    }

    public ObservableCollection<string> Tags { get; }

    public string? Draft
    {
        get => _draft;
        set => SetField(ref _draft, value);
    }

    public ICommand AddTag { get; }

    public ICommand RemoveTag { get; }

    /// <summary>Stands in for the app's tag index.</summary>
    public TagSuggestionProvider Suggest { get; }
}
