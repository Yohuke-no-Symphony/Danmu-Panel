using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Yohuke.DanmuPanel.ViewModels;

internal sealed class FeedCollection<T> : ObservableCollection<T>
{
    private static PropertyChangedEventArgs CountChanged { get; } = new(nameof(Count));
    private static PropertyChangedEventArgs ItemsChanged { get; } = new("Item[]");

    internal void InsertRange(int index, IReadOnlyList<T> values)
    {
        if (values.Count == 0)
        {
            return;
        }
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        CheckReentrancy();
        var added = values.ToArray();
        for (var offset = 0; offset < added.Length; offset++)
        {
            Items.Insert(index + offset, added[offset]);
        }
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(ItemsChanged);
        OnCollectionChanged(new(NotifyCollectionChangedAction.Add, added, index));
    }

    internal void RemoveRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count - index);
        if (count == 0)
        {
            return;
        }
        CheckReentrancy();
        var removed = this.Skip(index).Take(count).ToArray();
        for (var offset = count - 1; offset >= 0; offset--)
        {
            Items.RemoveAt(index + offset);
        }
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(ItemsChanged);
        OnCollectionChanged(new(NotifyCollectionChangedAction.Remove, removed, index));
    }
}
