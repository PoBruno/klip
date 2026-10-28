using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;

namespace Klip.App.ViewModels;

/// <summary>
/// ObservableCollection with batch helpers for the history list.
/// Reset swaps the whole content with one notification (first page, reload).
/// AppendRange raises one Add per item: WPF collection views reject multi item Add
/// events, and on a flat list a per item Add only touches the new rows, while a Reset
/// throws away every realized container and remeasures the list.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Appends items, one Add notification each, Count/indexer raised once.</summary>
    public void AppendRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;
        CheckReentrancy();
        foreach (var item in items)
        {
            var index = Items.Count;
            Items.Add(item);
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }
        RaiseCountChanged();
    }

    /// <summary>Clears and fills in one shot, with a single Reset.</summary>
    public void Reset(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Keeps only the first <paramref name="count"/> items (one Reset).</summary>
    public void TrimTo(int count)
    {
        if (count >= Items.Count)
            return;
        CheckReentrancy();
        while (Items.Count > count)
            Items.RemoveAt(Items.Count - 1);
        RaiseCountChanged();
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseCountChanged()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }
}
