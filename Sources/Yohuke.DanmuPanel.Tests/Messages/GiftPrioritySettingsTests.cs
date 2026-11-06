using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证消息入库携带新门槛后，历史礼物仍按当前阅读规则重新排序。</summary>
public sealed class GiftPrioritySettingsTests
{
    /// <summary>普通消息和测试消息提前保存新门槛，都不能使历史重算被跳过。</summary>
    [Theory]
    [InlineData(false, 100, 200, false)]
    [InlineData(false, 200, 100, true)]
    [InlineData(true, 100, 200, false)]
    [InlineData(true, 200, 100, true)]
    public async Task AppendingWithChangedOptionsDoesNotSkipGiftReevaluation(bool appendTestMessages,
        int previousMinimum, int nextMinimum, bool expectedPriority)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var previous = new MessageReadOptions { GiftAutoPinMinimum = previousMinimum };
        var next = previous with { GiftAutoPinMinimum = nextMinimum };
        var unread = Gift("unread");
        var otherRoom = Gift("other-room") with { RoomTabId = "other-tab" };
        var read = Gift("read");
        await store.AppendBatchAsync([unread, otherRoom, read], previous);
        await store.MarkReadAsync(read.Id, read.Quantity, read.TotalPrice);
        Assert.Equal(!expectedPriority, (await store.GetByIdAsync(unread.Id))!.IsPriority);

        var incoming = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu,
            Content = "新门槛下的消息", IsTest = appendTestMessages
        };
        if (appendTestMessages)
        {
            await store.AppendTestMessagesAsync([incoming], next);
        }
        else
        {
            await store.AppendAsync(incoming, next);
        }
        await store.ReevaluateReadOptionsAsync(next);

        foreach (var original in new[] { unread, otherRoom })
        {
            var updated = (await store.GetByIdAsync(original.Id))!;
            Assert.Equal(expectedPriority, updated.IsPriority);
            Assert.False(updated.IsRead);
            Assert.False(updated.HasBeenRead);
            Assert.Equal(original.TotalPrice, updated.TotalPrice);
            Assert.Equal(expectedPriority ? 1 : 0,
                (await store.LoadFeedAsync(original.RoomTabId, MessageKind.Gift, 100, null)).UnreadCount);
        }
        var confirmed = (await store.GetByIdAsync(read.Id))!;
        Assert.True(confirmed.IsRead);
        Assert.True(confirmed.HasBeenRead);
        Assert.False(confirmed.IsPriority);
        Assert.Equal(read.Quantity, confirmed.ReadQuantity);
        Assert.Equal(read.TotalPrice, confirmed.ReadTotalPrice);
    }

    /// <summary>批次失败时，门槛及已预热的历史优先级一起回滚。</summary>
    [Fact]
    public async Task FailedAppendRollsBackThresholdAndExistingGiftPriorities()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var previous = new MessageReadOptions { GiftAutoPinMinimum = 100 };
        await store.AppendAsync(Gift("existing"), previous);
        Assert.True(Assert.Single((await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null)).Records).IsPriority);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER reject_message BEFORE INSERT ON message_records WHEN NEW.id='rejected'
            BEGIN SELECT RAISE(ABORT,'message rejected'); END;
            """;
        command.ExecuteNonQuery();

        await Assert.ThrowsAsync<SqliteException>(() => store.AppendAsync(new()
        {
            Id = "rejected", RoomTabId = "tab", Kind = MessageKind.Danmu, Content = "无法提交的消息"
        }, previous with { GiftAutoPinMinimum = 200 }));

        var restored = Assert.Single((await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null)).Records);
        Assert.True(restored.IsPriority);
        Assert.False(restored.IsRead);
        command.CommandText = "SELECT json_extract(payload,'$.GiftAutoPinMinimum') FROM message_options WHERE id=1;";
        Assert.Equal(100L, command.ExecuteScalar());
        Assert.Null(await store.GetByIdAsync("rejected"));
    }

    private static MessageRecord Gift(string id)
    {
        return new()
        {
            Id = id, RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            UserUid = 42, UserName = "viewer", GiftId = "flower", GiftName = "鲜花",
            UnitPrice = 150, TotalPrice = 150
        };
    }
}
