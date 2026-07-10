using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Klip.App.ViewModels;

/// <summary>
/// ObservableCollection that can add a whole page at once with a single Reset
/// notification, instead of one CollectionChanged per item. Populating the
/// history flyout with 30 items used to fire 30 notifications (each reprocessed
/// by the grouped CollectionView); this makes it one.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Adds many items and raises a single Reset at the end.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item); // Items = the underlying list, no notification
            added = true;
        }
        if (added)
            RaiseReset();
    }

    /// <summary>Clears and fills in one shot, with a single Reset.</summary>
    public void Reset(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
