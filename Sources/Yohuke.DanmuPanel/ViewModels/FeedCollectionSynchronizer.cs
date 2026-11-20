using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Yohuke.DanmuPanel.ViewModels;

internal static class FeedCollectionSynchronizer
{
    internal static IReadOnlyList<FeedCollectionEdit<T>> Plan<T>(IReadOnlyList<T> current, IReadOnlyList<T> expected,
        CancellationToken cancellationToken, Predicate<T>? isPlaceholder = null, bool useRanges = true) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (current.SequenceEqual(expected, ReferenceEqualityComparer.Instance))
        {
            return [];
        }

        var working = current.ToList();
        var edits = new List<FeedCollectionEdit<T>>();
        var positions = new Dictionary<T, int>(expected.Count, ReferenceEqualityComparer.Instance);
        for (var index = 0; index < expected.Count; index++)
        {
            positions.Add(expected[index], index);
        }

        for (var index = working.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (positions.ContainsKey(working[index]) || isPlaceholder?.Invoke(working[index]) == true)
            {
                continue;
            }
            var first = index;
            while (first > 0 && !positions.ContainsKey(working[first - 1]) &&
                isPlaceholder?.Invoke(working[first - 1]) != true)
            {
                first--;
            }
            if (useRanges)
            {
                edits.Add(new(NotifyCollectionChangedAction.Remove, first, index - first + 1));
            }
            else
            {
                for (var removed = index; removed >= first; removed--)
                {
                    edits.Add(new(NotifyCollectionChangedAction.Remove, removed));
                }
            }
            working.RemoveRange(first, index - first + 1);
            index = first;
        }

        if (isPlaceholder is not null)
        {
            var retained = working.ToHashSet(ReferenceEqualityComparer.Instance);
            using var missing = expected.Where(item => !retained.Contains(item)).GetEnumerator();
            foreach (var placeholder in working.Where(item => !positions.ContainsKey(item)).ToArray())
            {
                var index = IndexOfReference(working, placeholder);
                if (missing.MoveNext())
                {
                    working[index] = missing.Current;
                    edits.Add(new(NotifyCollectionChangedAction.Replace, index, Values: [missing.Current]));
                }
                else
                {
                    working.RemoveAt(index);
                    edits.Add(new(NotifyCollectionChangedAction.Remove, index));
                }
            }
        }

        var retainedInOrder = true;
        var lastPosition = -1;
        foreach (var item in working)
        {
            var position = positions[item];
            if (position <= lastPosition)
            {
                retainedInOrder = false;
                break;
            }
            lastPosition = position;
        }
        if (retainedInOrder)
        {
            for (var index = 0; index < expected.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index < working.Count && ReferenceEquals(working[index], expected[index]))
                {
                    continue;
                }
                var nextRetained = index + 1;
                if (useRanges)
                {
                    while (nextRetained < expected.Count &&
                        (index >= working.Count || !ReferenceEquals(working[index], expected[nextRetained])))
                    {
                        nextRetained++;
                    }
                }
                var added = expected.Skip(index).Take(nextRetained - index).ToArray();
                working.InsertRange(index, added);
                edits.Add(new(NotifyCollectionChangedAction.Add, index, added.Length, added));
                index = nextRetained - 1;
            }
            return edits;
        }

        var stable = FindStableItems(working, positions);
        T? next = null;
        for (var index = expected.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = expected[index];
            if (!stable.Contains(item))
            {
                var previousIndex = IndexOfReference(working, item);
                var nextIndex = next is null ? working.Count : IndexOfReference(working, next);
                if (previousIndex < 0)
                {
                    working.Insert(nextIndex, item);
                    edits.Add(new(NotifyCollectionChangedAction.Add, nextIndex, Values: [item]));
                }
                else
                {
                    if (previousIndex < nextIndex)
                    {
                        nextIndex--;
                    }
                    if (previousIndex != nextIndex)
                    {
                        working.RemoveAt(previousIndex);
                        working.Insert(nextIndex, item);
                        edits.Add(new(NotifyCollectionChangedAction.Move, nextIndex, OldIndex: previousIndex));
                    }
                }
            }
            next = item;
        }
        return edits;
    }

    internal static async Task SynchronizeAsync<T>(ObservableCollection<T> target, IReadOnlyList<T> expected,
        CancellationToken cancellationToken, Predicate<T>? isPlaceholder = null) where T : class
    {
        var current = target.ToArray();
        var useRanges = target is FeedCollection<T>;
        var edits = await Task.Run(() => Plan(current, expected, cancellationToken, isPlaceholder, useRanges), cancellationToken);
        var budget = new FeedUiWorkBudget();
        foreach (var edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyEdit(target, edit, static value => value);
            await budget.YieldIfNeededAsync(cancellationToken);
        }
    }

    internal static void ApplyEdits<T, TSource>(ObservableCollection<T> target, IReadOnlyList<FeedCollectionEdit<TSource>> edits,
        Func<TSource, T> resolve) where T : class where TSource : class
    {
        foreach (var edit in edits)
        {
            ApplyEdit(target, edit, resolve);
        }
    }

    private static void ApplyEdit<T, TSource>(ObservableCollection<T> target, FeedCollectionEdit<TSource> edit,
        Func<TSource, T> resolve) where T : class where TSource : class
    {
        switch (edit.Action)
        {
            case NotifyCollectionChangedAction.Remove:
                if (target is FeedCollection<T> removalTarget)
                {
                    removalTarget.RemoveRange(edit.Index, edit.Count);
                }
                else
                {
                    target.RemoveAt(edit.Index);
                }
                break;
            case NotifyCollectionChangedAction.Add:
                if (target is FeedCollection<T> insertionTarget)
                {
                    insertionTarget.InsertRange(edit.Index, edit.Values!.Select(resolve).ToArray());
                }
                else
                {
                    target.Insert(edit.Index, resolve(edit.Values![0]));
                }
                break;
            case NotifyCollectionChangedAction.Replace:
                target[edit.Index] = resolve(edit.Values![0]);
                break;
            case NotifyCollectionChangedAction.Move:
                target.Move(edit.OldIndex, edit.Index);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edit));
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
            tails[lower] = index;
            previous[index] = lower == 0 ? -1 : tails[lower - 1];
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
