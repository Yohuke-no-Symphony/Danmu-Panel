using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证阅读状态在消息窗口分页前生效，并独立保留完整未读计数。</summary>
public sealed class FeedReadFilterTests
{
    /// <summary>未读与已读窗口按房间和类型隔离，已读分页不限制完整未读窗口。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ReadFiltersPartitionMessagesAndKeepScopeUnreadCount(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var records = Enumerable.Range(0, 5).Select(index => Message(kind, index)).ToArray();
        await store.AppendBatchAsync(records, new());
        await store.MarkReadAsync(records[1].Id, 1, 10);
        await store.MarkReadAsync(records[3].Id, 1, 10);
        await store.AppendAsync(Message(kind, 5) with { RoomTabId = "other" }, new());
        await store.AppendAsync(Message(kind == MessageKind.Gift ? MessageKind.SuperChat : MessageKind.Gift, 6), new());

        var all = await store.LoadFeedAsync("tab", kind, 1, null);
        var unread = await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Unread);
        var read = await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Read);

        Assert.Equal(new[] { records[0].Id, records[2].Id, records[4].Id, records[3].Id }, all.Records.Select(record => record.Id));
        Assert.Equal(new[] { records[0].Id, records[2].Id, records[4].Id }, unread.Records.Select(record => record.Id));
        Assert.Equal(records[3].Id, Assert.Single(read.Records).Id);
        Assert.All(unread.Records, record => Assert.False(record.IsRead));
        Assert.All(read.Records, record => Assert.True(record.IsRead));
        Assert.Equal(3, all.UnreadCount);
        Assert.Equal(3, unread.UnreadCount);
        Assert.Equal(3, read.UnreadCount);
    }

    /// <summary>单条确认后从未读窗口消失，进入已读窗口，重开数据库仍保留结果。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task MarkReadMovesMessageAndSurvivesReopening(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var message = Message(kind, 0);
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            await store.AppendBatchAsync([message, Message(kind, 1)], new());
            Assert.Equal(2, (await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Unread)).Records.Count);
            Assert.Empty((await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Read)).Records);

            await store.MarkReadAsync(message.Id, message.Quantity, message.TotalPrice);

            var unread = await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Unread);
            var read = await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Read);
            Assert.Equal("message-1", Assert.Single(unread.Records).Id);
            Assert.Equal(message.Id, Assert.Single(read.Records).Id);
            Assert.Equal(1, unread.UnreadCount);
            Assert.Equal(1, read.UnreadCount);
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var restored = await reopened.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Read);
        Assert.Equal(message.Id, Assert.Single(restored.Records).Id);
        Assert.Equal(1, restored.UnreadCount);
    }

    /// <summary>阅读状态独立于优先标记，两个分组均使用实际已读状态筛选。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ReadStateDoesNotDependOnPriorityFlag(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendBatchAsync(Enumerable.Range(0, 3).Select(index => Message(kind, index)).ToArray(), new());
        await store.MarkReadAsync("message-2", 1, 10);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE message_records SET priority=0,payload=json_set(payload,'$.IsPriority',json('false'))
            WHERE id='message-1';
            UPDATE message_records SET priority=1,payload=json_set(payload,'$.IsPriority',json('true'))
            WHERE id='message-2';
            """;
        command.ExecuteNonQuery();

        var unread = await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Unread);
        var read = await store.LoadFeedAsync("tab", kind, 1, null, readFilter: MessageReadFilter.Read);

        Assert.Equal(new[] { "message-0", "message-1" }, unread.Records.Select(record => record.Id));
        Assert.Equal("message-2", Assert.Single(read.Records).Id);
        Assert.Equal(2, unread.UnreadCount);
        Assert.Equal(2, read.UnreadCount);
    }

    /// <summary>相同时间戳下已读分页、保留边界和加载更多均按接收顺序稳定扩展。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ReadHistoryPagesUseFilteredBoundariesWithEqualTimestamps(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        await store.AppendBatchAsync(Enumerable.Range(0, 12)
            .Select(index => Message(kind, index) with { Time = time }).ToArray(), new());
        for (var index = 0; index < 12; index += 2)
        {
            await store.MarkReadAsync("message-" + index, 1, 10);
        }
        var initial = await store.LoadFeedAsync("tab", kind, 2, null, readFilter: MessageReadFilter.Read);
        Assert.Equal(new[] { "message-8", "message-10" }, initial.Records.Select(record => record.Id));

        await store.AppendBatchAsync([Message(kind, 12) with { Time = time }, Message(kind, 13) with { Time = time }], new());
        await store.MarkReadAsync("message-12", 1, 10);
        var retained = await store.LoadFeedAsync("tab", kind, 2, initial.Records[0].Id, readFilter: MessageReadFilter.Read);
        var expanded = await store.LoadFeedAsync("tab", kind, 5, initial.Records[0].Id, readFilter: MessageReadFilter.Read);

        Assert.Equal(new[] { "message-8", "message-10", "message-12" }, retained.Records.Select(record => record.Id));
        Assert.Equal(new[] { "message-4", "message-6", "message-8", "message-10", "message-12" }, expanded.Records.Select(record => record.Id));
        Assert.Equal(7, retained.UnreadCount);
        Assert.Equal(7, expanded.UnreadCount);
    }

    /// <summary>两个阅读窗口继续隐藏低于金额门槛的礼物，未读计数仅包括可见礼物。</summary>
    [Theory]
    [InlineData(MessageReadFilter.Unread, "message-1")]
    [InlineData(MessageReadFilter.Read, "message-3")]
    public async Task ReadFiltersRespectGiftVisibility(MessageReadFilter readFilter, string expectedId)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 10 };
        await store.AppendBatchAsync(Enumerable.Range(0, 4).Select(index => Message(MessageKind.Gift, index) with
        {
            UnitPrice = index % 2 == 0 ? 9 : 10,
            TotalPrice = index % 2 == 0 ? 9 : 10
        }).ToArray(), options);
        await store.MarkReadAsync("message-2", 1, 9);
        await store.MarkReadAsync("message-3", 1, 10);

        var snapshot = await store.LoadFeedAsync("tab", MessageKind.Gift, 1, null, readFilter: readFilter);

        Assert.Equal(expectedId, Assert.Single(snapshot.Records).Id);
        Assert.Equal(1, snapshot.UnreadCount);
        Assert.Equal(4, (await store.LoadPageAsync("tab", MessageKind.Gift, 0, 100)).Count);
    }

    /// <summary>礼物连击更新按照现有确认策略留在已读或重新进入未读窗口。</summary>
    [Theory]
    [InlineData(GiftReadResetPolicy.ConfirmOnce, 6, false)]
    [InlineData(GiftReadResetPolicy.NewQuantity, 3, true)]
    [InlineData(GiftReadResetPolicy.NewValueThreshold, 3, false)]
    [InlineData(GiftReadResetPolicy.NewValueThreshold, 4, true)]
    public async Task GiftComboUpdatesAppearOnlyInExpectedReadFilter(GiftReadResetPolicy policy, int quantity, bool isUnread)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 20, GiftReadResetPolicy = policy };
        var first = (await store.AppendAsync(Message(MessageKind.Gift, 0) with { ComboId = "combo", BatchComboCount = 2 }, options)).Record!;
        await store.MarkReadAsync(first.Id, first.Quantity, first.TotalPrice);
        Assert.Equal(first.Id, Assert.Single((await store.LoadFeedAsync("tab", MessageKind.Gift, 1, null,
            readFilter: MessageReadFilter.Read)).Records).Id);

        await store.AppendAsync(Message(MessageKind.Gift, 1) with { ComboId = "combo", BatchComboCount = quantity }, options);
        var unread = await store.LoadFeedAsync("tab", MessageKind.Gift, 1, null, readFilter: MessageReadFilter.Unread);
        var read = await store.LoadFeedAsync("tab", MessageKind.Gift, 1, null, readFilter: MessageReadFilter.Read);

        var current = Assert.Single(isUnread ? unread.Records : read.Records);
        Assert.Equal(first.Id, current.Id);
        Assert.Equal(quantity, current.Quantity);
        Assert.Empty(isUnread ? read.Records : unread.Records);
        Assert.Equal(isUnread ? 1 : 0, unread.UnreadCount);
        Assert.Equal(unread.UnreadCount, read.UnreadCount);
    }

    /// <summary>切换阅读筛选复用已验证正文和记录实例，不重复读取正文。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task SwitchingReadFiltersReusesMessagePayloads(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendBatchAsync(Enumerable.Range(0, 3).Select(index => Message(kind, index)).ToArray(), new());
        await store.MarkReadAsync("message-1", 1, 10);
        var all = (await store.LoadFeedAsync("tab", kind, 3, null)).Records.ToDictionary(record => record.Id);
        var rowsRead = store.PayloadRowsRead;
        var bytesRead = store.PayloadTextBytesRead;

        var unread = await store.LoadFeedAsync("tab", kind, 3, null, readFilter: MessageReadFilter.Unread);
        var read = await store.LoadFeedAsync("tab", kind, 3, null, readFilter: MessageReadFilter.Read);

        Assert.Equal(2, unread.Records.Count);
        Assert.Single(read.Records);
        Assert.All(unread.Records.Concat(read.Records), record => Assert.Same(all[record.Id], record));
        Assert.Equal(rowsRead, store.PayloadRowsRead);
        Assert.Equal(bytesRead, store.PayloadTextBytesRead);
    }

    /// <summary>普通弹幕忽略阅读筛选，继续保留置顶及历史窗口。</summary>
    [Theory]
    [InlineData(MessageReadFilter.Unread)]
    [InlineData(MessageReadFilter.Read)]
    public async Task DanmuIgnoresReadFilters(MessageReadFilter readFilter)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendBatchAsync([Message(MessageKind.Danmu, 0), Message(MessageKind.Danmu, 1)], new());
        await store.TogglePinAsync("message-1");

        var snapshot = await store.LoadFeedAsync("tab", MessageKind.Danmu, 1, null, readFilter: readFilter);

        Assert.Equal(new[] { "message-1", "message-0" }, snapshot.Records.Select(record => record.Id));
        Assert.Equal(0, snapshot.UnreadCount);
    }

    private static MessageRecord Message(MessageKind kind, int index)
    {
        return new()
        {
            Id = "message-" + index, RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserUid = 42, UserName = "viewer", Content = "谢谢支持", GiftId = "flower", GiftName = "鲜花",
            Quantity = 1, UnitPrice = 10, TotalPrice = 10,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        };
    }
}
