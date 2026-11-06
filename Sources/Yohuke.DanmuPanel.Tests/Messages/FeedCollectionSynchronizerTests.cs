using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证消息集合重排保持实例、增量通知及可取消的加载占位替换。</summary>
public sealed class FeedCollectionSynchronizerTests
{
    /// <summary>单条消息前后跨越多条历史时，只发送该消息的一次移动通知。</summary>
    [AvaloniaTheory]
    [InlineData(0, 19)]
    [InlineData(19, 0)]
    [InlineData(4, 15)]
    [InlineData(15, 4)]
    public async Task MovingOneItemDoesNotMoveItsNeighbors(int from, int to)
    {
        var originals = Enumerable.Range(0, 20).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>(originals);
        var expected = originals.ToList();
        var moved = expected[from];
        expected.RemoveAt(from);
        expected.Insert(to, moved);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        target.CollectionChanged += (_, args) => changes.Add(args);

        await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None);

        Assert.Equal(expected, target);
        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Move, change.Action);
        Assert.Same(moved, change.OldItems![0]);
    }

    /// <summary>遍历小集合的全部排列，始终保留原对象且不清空、删除或重建已有项。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySmallPermutationPreservesTheExistingItems(bool useRanges)
    {
        var originals = Enumerable.Range(0, 5).Select(_ => new object()).ToArray();
        ObservableCollection<object> target = useRanges ? new FeedCollection<object>() : new();
        foreach (var item in originals)
        {
            target.Add(item);
        }
        target.CollectionChanged += (_, args) => Assert.Equal(NotifyCollectionChangedAction.Move, args.Action);
        foreach (var expected in Permutations(originals))
        {
            await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None);

            Assert.Equal(expected, target);
        }
    }

    /// <summary>同时增删及重排消息后，后续相同快照不产生集合通知。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedChangesConvergeAndAnUnchangedSnapshotIsSilent(bool useRanges)
    {
        var originals = Enumerable.Range(0, 5).Select(_ => new object()).ToArray();
        ObservableCollection<object> target = useRanges ? new FeedCollection<object>() : new();
        foreach (var item in originals)
        {
            target.Add(item);
        }
        object[] expected = [new(), originals[4], originals[1], new(), originals[0]];
        target.CollectionChanged += (_, args) => Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);

        await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None);

        Assert.Equal(expected, target);
        var changes = 0;
        target.CollectionChanged += (_, _) => changes++;
        await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None);
        Assert.Equal(0, changes);
    }

    /// <summary>首屏和新增历史保持从前向后的增量插入，已出现消息不会被后续同批消息顶走。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedRowsReceiveNewMessagesInTheirDisplayOrder(bool retainExisting)
    {
        var retained = new object();
        var target = new ObservableCollection<object>();
        if (retainExisting)
        {
            target.Add(retained);
        }
        var added = Enumerable.Range(0, 40).Select(_ => new object()).ToArray();
        var expected = retainExisting ? [.. added, retained] : added;
        var inserted = new List<object>();
        target.CollectionChanged += (_, args) =>
        {
            Assert.Equal(NotifyCollectionChangedAction.Add, args.Action);
            inserted.Add(Assert.Single(args.NewItems!.Cast<object>()));
        };

        await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None);

        Assert.Equal(expected, target);
        Assert.Equal(added, inserted);
    }

    /// <summary>历史加载占位优先原位替换，剩余占位被移除且已有真实行不被搬动。</summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task LoadingPlaceholdersAreReplacedWithoutMovingExistingRows(int addedCount)
    {
        var placeholders = Enumerable.Range(0, 3).Select(_ => new object()).ToArray();
        var retained = new object();
        var target = new ObservableCollection<object>(placeholders) { retained };
        var expected = Enumerable.Range(0, addedCount).Select(_ => new object()).Append(retained).ToArray();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        target.CollectionChanged += (_, args) => changes.Add(args);

        await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None,
            placeholders.Contains);

        Assert.Equal(expected, target);
        Assert.Equal(Math.Min(addedCount, placeholders.Length),
            changes.Count(change => change.Action == NotifyCollectionChangedAction.Replace));
        Assert.DoesNotContain(changes, change => change.Action is NotifyCollectionChangedAction.Move or
            NotifyCollectionChangedAction.Reset);
    }

    /// <summary>集合通知中发生取消后停止继续修改，下一轮快照可以恢复完整结果。</summary>
    [AvaloniaFact]
    public async Task CancellationStopsMutationsAndTheNextSnapshotCanResume()
    {
        var originals = Enumerable.Range(0, 3).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>(originals);
        object[] expected = [originals[2], new()];
        var lifetime = new CancellationTokenSource();
        NotifyCollectionChangedEventHandler cancel = (_, _) => lifetime.Cancel();
        target.CollectionChanged += cancel;

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                FeedCollectionSynchronizer.SynchronizeAsync(target, expected, lifetime.Token));

            Assert.Equal(2, target.Count);
        }
        finally
        {
            target.CollectionChanged -= cancel;
            lifetime.Dispose();
        }
        await FeedCollectionSynchronizer.SynchronizeAsync(target, expected, CancellationToken.None);
        Assert.Equal(expected, target);
    }

    private static IEnumerable<object[]> Permutations(object[] items)
    {
        if (items.Length == 0)
        {
            yield return [];
            yield break;
        }
        for (var index = 0; index < items.Length; index++)
        {
            var excludedIndex = index;
            foreach (var remaining in Permutations([.. items.Where((_, itemIndex) => itemIndex != excludedIndex)]))
            {
                yield return [items[index], .. remaining];
            }
        }
    }
}
