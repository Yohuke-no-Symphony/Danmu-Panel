using System.Collections.ObjectModel;

namespace Yohuke.DanmuPanel.ViewModels;

internal static class FeedCollectionSynchronizer
{
    internal static async Task SynchronizeAsync<T>(ObservableCollection<T> target, IReadOnlyList<T> expected,
        CancellationToken cancellationToken, Predicate<T>? isPlaceholder = null) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.SequenceEqual(expected, ReferenceEqualityComparer.Instance))
        {
            return;
        }
        var positions = new Dictionary<T, int>(expected.Count, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < expected.Count; index++)
        {
            positions.Add(expected[index], index);
        }
        var uiBudget = new FeedUiWorkBudget();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (positions.ContainsKey(target[index]) || isPlaceholder?.Invoke(target[index]) == true)
            {
                continue;
            }
            var first = index;
            while (first > 0 && !positions.ContainsKey(target[first - 1]) &&
                isPlaceholder?.Invoke(target[first - 1]) != true)
            {
                first--;
            }
            if (target is FeedCollection<T> rangeTarget)
            {
                rangeTarget.RemoveRange(first, index - first + 1);
                await uiBudget.YieldIfNeededAsync(cancellationToken);
            }
            else
            {
                for (var removedIndex = index; removedIndex >= first; removedIndex--)
                {
                    target.RemoveAt(removedIndex);
                    await uiBudget.YieldIfNeededAsync(cancellationToken);
                }
            }
            index = first;
        }
        if (isPlaceholder is not null)
        {
            var retained = target.ToHashSet(ReferenceEqualityComparer.Instance);
            using var missing = expected.Where(item => !retained.Contains(item)).GetEnumerator();
            foreach (var placeholder in target.Where(item => !positions.ContainsKey(item)).ToArray())
            {
                var index = IndexOfReference(target, placeholder);
                if (missing.MoveNext())
                {
                    target[index] = missing.Current;
                }
                else
                {
                    target.RemoveAt(index);
                }
                await uiBudget.YieldIfNeededAsync(cancellationToken);
            }
        }
        var stable = FindStableItems(target, positions);
        if (stable.Count == target.Count)
        {
            for (var index = 0; index < expected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index < target.Count && ReferenceEquals(target[index], expected[index]))
                {
                    continue;
                }
                if (target is FeedCollection<T> rangeTarget)
                {
                    var nextRetained = index + 1;
                    while (nextRetained < expected.Count &&
                        (index >= target.Count || !ReferenceEquals(target[index], expected[nextRetained])))
                    {
                        nextRetained++;
                    }
                    rangeTarget.InsertRange(index, expected.Skip(index).Take(nextRetained - index).ToArray());
                    index = nextRetained - 1;
                }
                else
                {
                    target.Insert(index, expected[index]);
                }
                await uiBudget.YieldIfNeededAsync(cancellationToken);
            }
            return;
        }
        T? next = null;
        for (var index = expected.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = expected[index];
            if (!stable.Contains(item))
            {
                var previousIndex = IndexOfReference(target, item);
                var nextIndex = next is null ? target.Count : IndexOfReference(target, next);
                if (previousIndex < 0)
                {
                    target.Insert(nextIndex, item);
                    await uiBudget.YieldIfNeededAsync(cancellationToken);
                }
                else
                {
                    if (previousIndex < nextIndex)
                    {
                        nextIndex--;
                    }
                    if (previousIndex != nextIndex)
                    {
                        target.Move(previousIndex, nextIndex);
                        await uiBudget.YieldIfNeededAsync(cancellationToken);
                    }
                }
            }
            next = item;
        }
    }

    private static HashSet<T> FindStableItems<T>(IReadOnlyList<T> target, IReadOnlyDictionary<T, int> positions)
        where T : class
    {
        var stable = new HashSet<T>(ReferenceEqualityComparer.Instance);
        if (target.Count == 0)
        {
            return stable;
        }
        var tails = new int[target.Count];
        var previous = new int[target.Count];
        var length = 0;
        for (var index = 0; index < target.Count; index++)
        {
            var position = positions[target[index]];
            var lower = 0;
            var upper = length;
            while (lower < upper)
            {
                var middle = lower + (upper - lower) / 2;
                if (positions[target[tails[middle]]] < position)
                {
                    lower = middle + 1;
                }
                else
                {
                    upper = middle;
                }
            }
            previous[index] = lower == 0 ? -1 : tails[lower - 1];
            tails[lower] = index;
            if (lower == length)
            {
                length++;
            }
        }
        for (var index = tails[length - 1]; index >= 0; index = previous[index])
        {
            stable.Add(target[index]);
        }
        return stable;
    }

    private static int IndexOfReference<T>(IList<T> items, T item) where T : class
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], item))
            {
                return index;
            }
        }
        return -1;
    }
}
