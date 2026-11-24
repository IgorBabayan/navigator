using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Navigator.Presentation.Collections;

/// <summary>
/// ObservableCollection with batched updates: one notification for a whole batch instead of one per item,
/// which keeps 100k-item folders from generating 100k layout invalidations.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection()
    {
    }

    public RangeObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Replaces all items with a single Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        RaiseReset();
    }

    /// <summary>Appends items with a single multi-item Add notification.</summary>
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;
        CheckReentrancy();
        var start = Items.Count;
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items is System.Collections.IList list ? list : items.ToList(), start));
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
