using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace HomeVideoLibrary.Desktop.ViewModels;

/// <summary>An observable list that converges on a desired order of item instances with minimal Remove, Move and
/// Insert notifications, so bound containers (and their scroll position and focus) survive small changes. A large change
/// (a new search, another album) is published as one Reset notification instead.</summary>
public sealed class PatchableCollection<T> : ObservableCollection<T> where T : class
{
    /// <summary>Beyond this many moves or inserts a single Reset is cheaper for the bound list than individual events.</summary>
    private const int MaxIncrementalChanges = 64;

    private void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Makes the list equal <paramref name="desired"/> (compared by reference).</summary>
    public void Patch(IReadOnlyList<T> desired)
    {
        var wanted = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        var stale = 0;
        foreach (var item in Items) if (!wanted.Contains(item)) stale++;
        if (stale > MaxIncrementalChanges || Math.Abs(desired.Count - (Count - stale)) > MaxIncrementalChanges)
        {
            ReplaceAll(desired);
            return;
        }
        for (var index = Count - 1; index >= 0; index--)
            if (!wanted.Contains(this[index])) RemoveAt(index);
        var changes = 0;
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < Count && ReferenceEquals(this[index], desired[index])) continue;
            if (++changes > MaxIncrementalChanges)
            {
                ReplaceAll(desired);
                return;
            }
            var existing = IndexOf(desired[index], index + 1);
            if (existing >= 0) Move(existing, index);
            else Insert(index, desired[index]);
        }
    }

    private int IndexOf(T item, int start)
    {
        for (var index = start; index < Count; index++)
            if (ReferenceEquals(this[index], item)) return index;
        return -1;
    }
}
