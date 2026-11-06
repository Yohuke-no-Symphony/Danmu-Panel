using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证旧数据库中未完成重算的礼物优先级会在升级时一次性修复。</summary>
public sealed class GiftPriorityMigrationTests
{
    /// <summary>版本三已经保存新门槛时仍修复旧优先级，保留已读确认且重开不重复写入。</summary>
    [Theory]
    [InlineData(100, 200, false)]
    [InlineData(200, 100, true)]
    public async Task VersionThreeRepairsStaleGiftPrioritiesOnce(int previousMinimum,
        int savedMinimum, bool expectedPriority)
    {
        using var directory = new MessageStoreTestDirectory();
        await using (var original = new SqliteMessageStore(directory.DatabasePath))
        {
            var options = new MessageReadOptions { GiftAutoPinMinimum = previousMinimum };
            await original.AppendBatchAsync([Gift("unread"), Gift("read")], options);
            await original.MarkReadAsync("read", 1, 150);
        }
        using (var connection = Open(directory))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE message_options SET payload=$options WHERE id=1;
                PRAGMA user_version=3;
                CREATE TRIGGER reject_read_changes BEFORE UPDATE ON message_records WHEN NEW.id='read'
                BEGIN SELECT RAISE(ABORT,'read gift was changed during migration'); END;
                CREATE TRIGGER reject_options_changes BEFORE UPDATE ON message_options
                BEGIN SELECT RAISE(ABORT,'saved options were changed during migration'); END;
                """;
            command.Parameters.AddWithValue("$options", JsonSerializer.Serialize(
                new MessageReadOptions { GiftAutoPinMinimum = savedMinimum }));
            command.ExecuteNonQuery();
        }

        await using (var upgraded = new SqliteMessageStore(directory.DatabasePath))
        {
            var snapshot = await upgraded.LoadFeedAsync("tab", MessageKind.Gift, 100, null);
            Assert.Equal(expectedPriority ? 2 : 0, snapshot.Records.Count);
            var unread = (await upgraded.GetByIdAsync("unread"))!;
            Assert.Equal(expectedPriority, unread.IsPriority);
            Assert.False(unread.IsRead);
            Assert.False(unread.HasBeenRead);
            Assert.Equal(150m, unread.TotalPrice);
            Assert.Equal(expectedPriority ? 1 : 0, snapshot.UnreadCount);
            var read = (await upgraded.GetByIdAsync("read"))!;
            Assert.True(read.IsRead);
            Assert.True(read.HasBeenRead);
            Assert.False(read.IsPriority);
            Assert.Equal(1, read.ReadQuantity);
            Assert.Equal(150m, read.ReadTotalPrice);
        }
        using (var connection = Open(directory))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert.Equal(4L, command.ExecuteScalar());
            command.CommandText = "SELECT priority FROM message_records WHERE id='unread';";
            Assert.Equal(expectedPriority ? 1L : 0L, command.ExecuteScalar());
            command.CommandText = """
                CREATE TRIGGER reject_repeat_changes BEFORE UPDATE ON message_records
                BEGIN SELECT RAISE(ABORT,'migration was repeated'); END;
                """;
            command.ExecuteNonQuery();
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        Assert.Equal(expectedPriority, (await reopened.GetByIdAsync("unread"))!.IsPriority);
    }

    private static MessageRecord Gift(string id)
    {
        return new()
        {
            Id = id, RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            GiftId = "flower", GiftName = "鲜花", UnitPrice = 150, TotalPrice = 150
        };
    }

    private static SqliteConnection Open(MessageStoreTestDirectory directory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
