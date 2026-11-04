using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证无关设置及重复读取规则不会重写持久消息。</summary>
public sealed class MessageStoreWriteAmplificationTests
{
    /// <summary>重启后应用相同阅读设置不更新选项或礼物历史。</summary>
    [Fact]
    public async Task UnchangedReadOptionsDoNotWriteAfterReopening()
    {
        using var directory = new MessageStoreTestDirectory();
        var options = new MessageReadOptions { GiftAutoPinMinimum = 100 };
        await using (var initial = new SqliteMessageStore(directory.DatabasePath))
        {
            await initial.AppendAsync(Gift("gift", 150), options);
        }
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.GetGenerationAsync("room");
        using var connection = Open(directory);
        Execute(connection, """
            CREATE TRIGGER reject_options BEFORE UPDATE ON message_options
            BEGIN SELECT RAISE(ABORT,'unchanged options were rewritten'); END;
            CREATE TRIGGER reject_message BEFORE UPDATE ON message_records
            BEGIN SELECT RAISE(ABORT,'unchanged gift was rewritten'); END;
            """);

        await store.ReevaluateReadOptionsAsync(options);

        Assert.True((await store.GetByIdAsync("gift"))!.IsPriority);
    }

    /// <summary>连续新消息复用持久阅读规则，不逐条更新同一选项行。</summary>
    [Fact]
    public async Task AppendingWithUnchangedOptionsDoesNotRewriteOptions()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 100 };
        await store.AppendAsync(Gift("first", 150), options);
        using var connection = Open(directory);
        Execute(connection, """
            CREATE TRIGGER reject_options BEFORE UPDATE ON message_options
            BEGIN SELECT RAISE(ABORT,'unchanged options were rewritten'); END;
            """);

        await store.AppendAsync(Gift("second", 200), options);

        Assert.NotNull(await store.GetByIdAsync("second"));
    }

    /// <summary>阈值变化只更新跨越阈值的礼物，其他记录保持不动。</summary>
    [Fact]
    public async Task ThresholdChangeOnlyWritesChangedPriorities()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 100 };
        await store.AppendAsync(Gift("below", 20), options);
        await store.AppendAsync(Gift("crossing", 150), options);
        await store.AppendAsync(Gift("above", 300), options);
        using var connection = Open(directory);
        Execute(connection, """
            CREATE TRIGGER reject_unchanged BEFORE UPDATE ON message_records WHEN NEW.id <> 'crossing'
            BEGIN SELECT RAISE(ABORT,'unaffected gift was rewritten'); END;
            """);

        await store.ReevaluateReadOptionsAsync(options with { GiftAutoPinMinimum = 200 });

        Assert.False((await store.GetByIdAsync("below"))!.IsPriority);
        Assert.False((await store.GetByIdAsync("crossing"))!.IsPriority);
        Assert.True((await store.GetByIdAsync("above"))!.IsPriority);
    }

    /// <summary>只修改再次未读的策略时保存规则，不更新现有礼物优先级。</summary>
    [Fact]
    public async Task ReadResetPolicyChangeDoesNotRewriteGiftHistory()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var options = new MessageReadOptions { GiftAutoPinMinimum = 100 };
        await store.AppendAsync(Gift("gift", 150) with { ComboId = "combo", BatchComboCount = 1 }, options);
        await store.MarkReadAsync("gift", 1, 150);
        using var connection = Open(directory);
        Execute(connection, """
            CREATE TRIGGER reject_message BEFORE UPDATE ON message_records
            BEGIN SELECT RAISE(ABORT,'gift was rewritten for an unrelated setting'); END;
            """);

        await store.ReevaluateReadOptionsAsync(options with { GiftReadResetPolicy = GiftReadResetPolicy.NewQuantity });

        Assert.True((await store.GetByIdAsync("gift"))!.IsRead);
    }

    private static MessageRecord Gift(string id, decimal price)
    {
        return new()
        {
            Id = id, RoomTabId = "room", RoomId = "123", Kind = MessageKind.Gift,
            UserName = "viewer", GiftName = "gift", UnitPrice = price, TotalPrice = price
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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
