using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证全量已读事务、分页外计数与礼物连击的阅读确认。</summary>
public sealed class MarkAllReadTests
{
    /// <summary>全部已读覆盖分页外低价礼物，限定房间和类型，并持久化确认数量与价值。</summary>
    [Theory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task ReadsEntireScopeAndPreservesOtherMessages(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        var options = new MessageReadOptions { GiftAutoPinMinimum = 50 };
        var otherKind = kind == MessageKind.Gift ? MessageKind.SuperChat : MessageKind.Gift;
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            for (var index = 0; index < 105; index++)
            {
                await store.AppendAsync(Message(kind) with { Quantity = 3, TotalPrice = 30 }, options);
            }
            await store.AppendAsync(Message(kind) with { RoomTabId = "other" }, options);
            await store.AppendAsync(Message(otherKind), options);
            var snapshot = await store.LoadFeedAsync("tab", kind, 100, null);
            Assert.Equal(kind == MessageKind.Gift ? 0 : 105, snapshot.UnreadCount);
            Assert.Equal(kind == MessageKind.Gift ? 0 : 105, snapshot.Records.Count);

            Assert.Equal(105, await store.MarkAllReadAsync("tab", kind));
            Assert.Equal(0, await store.MarkAllReadAsync("tab", kind));
            Assert.Equal(0, (await store.LoadFeedAsync("tab", kind, 100, null)).UnreadCount);
            Assert.False(Assert.Single(await store.LoadPageAsync("other", kind, 0, 100)).IsRead);
            Assert.False(Assert.Single(await store.LoadPageAsync("tab", otherKind, 0, 100)).IsRead);
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var records = await reopened.LoadPageAsync("tab", kind, 0, 200);
        Assert.Equal(105, records.Count);
        Assert.All(records, record =>
        {
            Assert.True(record.IsRead);
            Assert.True(record.HasBeenRead);
            Assert.False(record.IsPriority);
            Assert.Equal(record.Quantity, record.ReadQuantity);
            Assert.Equal(record.TotalPrice, record.ReadTotalPrice);
        });
        await reopened.AppendAsync(Message(kind), options);
        Assert.Equal(kind == MessageKind.Gift ? 0 : 1, (await reopened.LoadFeedAsync("tab", kind, 100, null)).UnreadCount);
        if (kind == MessageKind.Gift)
        {
            await reopened.AppendAsync(Message(kind) with { UnitPrice = 50, TotalPrice = 50 }, options);
            Assert.Equal(1, (await reopened.LoadFeedAsync("tab", kind, 100, null)).UnreadCount);
        }
    }

    /// <summary>批量确认后新增连击数量，三种阅读策略仍按已确认的数量和价值决定是否恢复未读。</summary>
    [Theory]
    [InlineData(GiftReadResetPolicy.ConfirmOnce, 6, false)]
    [InlineData(GiftReadResetPolicy.NewQuantity, 3, true)]
    [InlineData(GiftReadResetPolicy.NewValueThreshold, 3, false)]
    [InlineData(GiftReadResetPolicy.NewValueThreshold, 4, true)]
    public async Task NewComboRetainsReadPolicy(GiftReadResetPolicy policy, int quantity, bool unread)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 20, GiftReadResetPolicy = policy };
        var first = await store.AppendAsync(Message(MessageKind.Gift) with { ComboId = "combo", BatchComboCount = 2 }, options);
        Assert.Equal(1, await store.MarkAllReadAsync("tab", MessageKind.Gift));
        var next = await store.AppendAsync(Message(MessageKind.Gift) with { ComboId = "combo", BatchComboCount = quantity }, options);

        Assert.Equal(first.Record!.Id, next.Record!.Id);
        Assert.Equal(unread, !next.Record.IsRead);
        Assert.Equal(2, next.Record.ReadQuantity);
        Assert.Equal(20, next.Record.ReadTotalPrice);
        Assert.Equal(unread ? 1 : 0, (await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null)).UnreadCount);
    }

    /// <summary>未读查询使用专用索引，避免每次刷新扫描和反序列化房间的全部历史。</summary>
    [Fact]
    public async Task UnreadCountUsesIndexedJsonReadState()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(MessageKind.Gift), new());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = directory.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "EXPLAIN QUERY PLAN SELECT COUNT(*) FROM message_records WHERE room_tab_id='tab' AND kind=2 AND json_extract(payload,'$.IsRead')=0;";
        using var reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Contains("ix_message_unread", reader.GetString(3));
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, UserUid = 42,
            GiftId = "rose", GiftName = "Rose", Quantity = 1, UnitPrice = 10, TotalPrice = 10
        };
    }
}
