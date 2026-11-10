namespace HomeVideoLibrary.Core;

/// <summary>The one definition of album-name identity, shared by the catalog's unique index, its migration and the model.
/// Album names follow the same rules as tags: spelling is trimmed with whitespace collapsed, and two names are the same
/// album when they differ only in case or diacritics ("École" and "ecole").</summary>
public static class AlbumNames
{
    /// <summary>The spelling to store, or null when nothing remains.</summary>
    public static string? Normalize(string? name) => TagRules.Normalize(name);

    public static string Key(string name) => TagRules.Key(name);

    /// <summary>Display order: by identity, then spelling.</summary>
    public static IOrderedEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, string> name) =>
        items.OrderBy(item => Key(name(item)), StringComparer.Ordinal).ThenBy(item => name(item), StringComparer.Ordinal);
}
