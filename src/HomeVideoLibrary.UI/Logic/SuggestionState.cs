using CoreTagRules = HomeVideoLibrary.Core.TagRules;

namespace HomeVideoLibrary.UI.Logic;

/// <summary>
/// The tag suggestion list's interaction state as pure transitions. The highlight is the tag itself, not
/// a row index, so it survives re-ranking; it clears when its tag is no longer offered.
/// </summary>
/// <param name="IsBrowsing">Down on an empty entry asked for every suggestion.</param>
/// <param name="IsDismissed">Escape hid the list until the text changes.</param>
public sealed record SuggestionState(bool IsBrowsing, bool IsDismissed, string? Highlighted)
{
    /// <summary>No list, no highlight: the state after focus leaves or a tag is committed.</summary>
    public static SuggestionState Closed { get; } = new(false, false, null);

    /// <summary>Whether suggestions should be offered for a normalized query (null when the entry is empty).</summary>
    public bool WantsList(string? query) => !IsDismissed && (IsBrowsing || query is not null);

    /// <summary>Typing reopens a dismissed list and drops the highlight; browsing ends once there is text.</summary>
    public SuggestionState TextChanged(string? query) => new(IsBrowsing && query is null, false, null);

    public SuggestionState Dismiss() => this with { IsDismissed = true, Highlighted = null };

    public SuggestionState Browse() => this with { IsBrowsing = true, IsDismissed = false };

    /// <summary>Steps the highlight through <paramref name="offered"/>, wrapping at both ends. With nothing
    /// highlighted, a forward step picks the first tag and a backward step the last.</summary>
    public SuggestionState Move(IReadOnlyList<string> offered, int step)
    {
        if (offered.Count == 0)
        {
            return this with { Highlighted = null };
        }

        var index = IndexOf(offered, Highlighted);
        var next = index < 0
            ? (step > 0 ? 0 : offered.Count - 1)
            : (((index + step) % offered.Count) + offered.Count) % offered.Count;
        return this with { Highlighted = offered[next] };
    }

    public SuggestionState Reconcile(IReadOnlyList<string> offered) =>
        IndexOf(offered, Highlighted) >= 0 ? this : this with { Highlighted = null };

    private static int IndexOf(IReadOnlyList<string> tags, string? tag)
    {
        if (tag is null)
        {
            return -1;
        }

        for (var i = 0; i < tags.Count; i++)
        {
            if (CoreTagRules.Comparer.Equals(tags[i], tag))
            {
                return i;
            }
        }

        return -1;
    }
}
