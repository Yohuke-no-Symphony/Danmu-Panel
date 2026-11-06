using System.Collections.Specialized;
using System.ComponentModel;
using Xunit;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证范围变更携带完整条目快照与索引，并同步更新绑定所需的属性。</summary>
public sealed class FeedCollectionTests
{
    /// <summary>范围插入和删除发布准确的新旧条目及位置，通知发生时集合已完整更新。</summary>
    [Fact]
    public void RangeNotificationsCarryCompleteSnapshotsAndBindingProperties()
    {
        var first = new object();
        var last = new object();
        var collection = new FeedCollection<object> { first, last };
        var added = new List<object> { new(), new(), new() };
        var originals = added.ToArray();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        var snapshots = new List<object[]>();
        var properties = new List<string?>();
        collection.CollectionChanged += (_, args) =>
        {
            changes.Add(args);
            snapshots.Add(collection.ToArray());
        };
        ((INotifyPropertyChanged)collection).PropertyChanged += (_, args) => properties.Add(args.PropertyName);

        collection.InsertRange(1, added);
        added.Clear();
        collection.RemoveRange(1, 3);

        Assert.Equal(new[] { first, last }, collection);
        object[] expanded = [first, .. originals, last];
        Assert.Equal(expanded, snapshots[0]);
        Assert.Equal(new[] { first, last }, snapshots[1]);
        Assert.Equal(new[] { "Count", "Item[]", "Count", "Item[]" }, properties);
        Assert.Equal(2, changes.Count);
        Assert.Equal(NotifyCollectionChangedAction.Add, changes[0].Action);
        Assert.Equal(1, changes[0].NewStartingIndex);
        Assert.Equal(originals, changes[0].NewItems!.Cast<object>());
        Assert.Equal(NotifyCollectionChangedAction.Remove, changes[1].Action);
        Assert.Equal(1, changes[1].OldStartingIndex);
        Assert.Equal(originals, changes[1].OldItems!.Cast<object>());
    }

    /// <summary>空范围不刷新界面，越界请求在改变集合或发布通知前失败。</summary>
    [Fact]
    public void EmptyAndInvalidRangesDoNotPublishChanges()
    {
        var original = new object();
        var collection = new FeedCollection<object> { original };
        var changes = 0;
        collection.CollectionChanged += (_, _) => changes++;

        collection.InsertRange(0, []);
        collection.RemoveRange(1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => collection.InsertRange(2, [new()]));
        Assert.Throws<ArgumentOutOfRangeException>(() => collection.RemoveRange(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => collection.RemoveRange(-1, 1));

        Assert.Same(original, Assert.Single(collection));
        Assert.Equal(0, changes);
    }
}
