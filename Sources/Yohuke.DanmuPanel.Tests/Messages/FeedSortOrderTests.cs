using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证排序方向不改变分页成员、历史边界或缓存身份。</summary>
public sealed class FeedSortOrderTests
{
    /// <summary>同一毫秒按接收顺序稳定排序，两个方向共享同一分页及完整未读计数。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat, MessageSortOrder.OldestFirst)]
    [InlineData(MessageKind.SuperChat, MessageSortOrder.NewestFirst)]
    [InlineData(MessageKind.Gift, MessageSortOrder.OldestFirst)]
    [InlineData(MessageKind.Gift, MessageSortOrder.NewestFirst)]
    [InlineData(MessageKind.Guard, MessageSortOrder.OldestFirst)]
    [InlineData(MessageKind.Guard, MessageSortOrder.NewestFirst)]
    public async Task SortingIsStableWithinTheSameTimestampAndKeepsTheSelectedWindow(MessageKind kind, MessageSortOrder sortOrder)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendBatchAsync(Enumerable.Range(0, 8).Select(index => Message(kind, index) with
        {
            Time = DateTimeOffset.FromUnixTimeMilliseconds(1800000000000 + index / 4)
        }).ToArray(), new());
        for (var index = 1; index < 8; index += 2)
        {
            await store.MarkReadAsync("message-" + index, 1, 30);
        }

        var unread = await store.LoadFeedAsync("tab", kind, 2, null,
            readFilter: MessageReadFilter.Unread, sortOrder: sortOrder);
        var read = await store.LoadFeedAsync("tab", kind, 2, null,
            readFilter: MessageReadFilter.Read, sortOrder: sortOrder);

        var unreadIds = new[] { "message-0", "message-2", "message-4", "message-6" };
        var readIds = new[] { "message-5", "message-7" };
        Assert.Equal(sortOrder == MessageSortOrder.OldestFirst ? unreadIds : unreadIds.Reverse(), unread.Records.Select(record => record.Id));
        Assert.Equal(sortOrder == MessageSortOrder.OldestFirst ? readIds : readIds.Reverse(), read.Records.Select(record => record.Id));
        Assert.Equal("message-5", read.OldestHistoryId);
        Assert.Null(unread.OldestHistoryId);
        Assert.Equal(4, unread.UnreadCount);
        Assert.Equal(4, read.UnreadCount);
    }

    /// <summary>倒序窗口保留实际最旧边界，加载更早记录不漏同时间戳的消息，切换方向复用缓存。</summary>
    [Theory]
    [InlineData(MessageSortOrder.OldestFirst)]
    [InlineData(MessageSortOrder.NewestFirst)]
    public async Task HistoryBoundaryAndPayloadCacheAreIndependentOfDisplayOrder(MessageSortOrder sortOrder)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        foreach (var batch in Enumerable.Range(0, 250).Select(index => Message(MessageKind.SuperChat, index) with { Time = time })
                     .Chunk(IMessageStore.MAX_BATCH_COUNT))
        {
            await store.AppendBatchAsync(batch, new());
        }
        await store.MarkAllReadAsync("tab", MessageKind.SuperChat);
        var first = await store.LoadFeedAsync("tab", MessageKind.SuperChat, 50, null,
            readFilter: MessageReadFilter.Read, sortOrder: sortOrder);
        Assert.Equal("message-200", first.OldestHistoryId);
        var originals = first.Records.ToDictionary(record => record.Id);
        var reads = store.PayloadRowsRead;
        var opposite = sortOrder == MessageSortOrder.OldestFirst ? MessageSortOrder.NewestFirst : MessageSortOrder.OldestFirst;
        var reversed = await store.LoadFeedAsync("tab", MessageKind.SuperChat, 50, null,
            readFilter: MessageReadFilter.Read, sortOrder: opposite);
        Assert.Equal(first.Records.Reverse(), reversed.Records);
        Assert.Equal(reads, store.PayloadRowsRead);
        Assert.All(reversed.Records, record => Assert.Same(originals[record.Id], record));

        await store.AppendAsync(Message(MessageKind.SuperChat, 250) with { Time = time }, new());
        await store.MarkReadAsync("message-250", 1, 30);
        var retained = await store.LoadFeedAsync("tab", MessageKind.SuperChat, 50, first.OldestHistoryId,
            readFilter: MessageReadFilter.Read, sortOrder: sortOrder);
        Assert.Equal(51, retained.Records.Count);
        Assert.Equal(first.OldestHistoryId, retained.OldestHistoryId);
        var expanded = await store.LoadFeedAsync("tab", MessageKind.SuperChat, 151, retained.OldestHistoryId,
            readFilter: MessageReadFilter.Read, sortOrder: sortOrder);
        Assert.Equal(151, expanded.Records.Count);
        Assert.Equal("message-100", expanded.OldestHistoryId);
        Assert.Equal(151, expanded.Records.Select(record => record.Id).Distinct().Count());
        Assert.All(first.Records, record => Assert.Same(record, expanded.Records.Single(current => current.Id == record.Id)));
    }

    private static MessageRecord Message(MessageKind kind, int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "viewer", GiftName = "鲜花", UnitPrice = 30, TotalPrice = 30,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }
}
