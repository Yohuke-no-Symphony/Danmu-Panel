using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证有界消息批次的提交次数、回滚与逐条去重语义。</summary>
public sealed class BatchMessageStoreTests
{
    /// <summary>满批次仅提交一个事务，返回后其他连接即可恢复全部消息。</summary>
    [Fact]
    public async Task FullBatchCommitsOnceAndIsImmediatelyRecoverable()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var messages = Enumerable.Range(0, IMessageStore.MAX_BATCH_COUNT).Select(index => Message($"message-{index}")).ToArray();

        var results = await store.AppendBatchAsync(messages, new(), "configuration");

        Assert.Equal(1, store.AppendTransactionCount);
        Assert.Equal(messages.Select(message => message.Id), results.Select(result => result.Record!.Id));
        Assert.All(results, result => Assert.Equal(MessageAppendDisposition.Appended, result.Disposition));
        await using var recovered = new SqliteMessageStore(directory.DatabasePath);
        Assert.Equal(messages.Length, (await recovered.LoadPageAsync("room", MessageKind.Danmu, 0, messages.Length)).Count);
        Assert.Equal(messages.Length, (await recovered.GetPendingAsync(messages.Length)).Count);
    }

    /// <summary>批次中途失败时，先前的消息、来源去重记录和翻译任务一并回滚。</summary>
    [Fact]
    public async Task FailedBatchRollsBackEveryMessageEventAndJob()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.GetGenerationAsync("room");
        using var connection = Open(directory);
        Execute(connection, """
            CREATE TRIGGER reject_last BEFORE INSERT ON message_records WHEN NEW.id='last'
            BEGIN SELECT RAISE(ABORT,'last message rejected'); END;
            """);
        var messages = new[] { Message("first"), Message("last") };

        await Assert.ThrowsAsync<SqliteException>(() => store.AppendBatchAsync(messages, new(), "configuration"));

        Assert.Equal(0, store.AppendTransactionCount);
        Assert.Empty(await store.LoadPageAsync("room", MessageKind.Danmu, 0, 10));
        Assert.Empty(await store.GetPendingAsync(10));
        Execute(connection, "DROP TRIGGER reject_last;");
        Assert.All(await store.AppendBatchAsync(messages, new(), "configuration"),
            result => Assert.Equal(MessageAppendDisposition.Appended, result.Disposition));
    }

    /// <summary>重复行的未提交来源 ID 不影响同一批次之后真正的新消息。</summary>
    [Fact]
    public async Task DuplicateRowDoesNotReserveItsNewSourceEvent()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message("existing"), new());

        var results = await store.AppendBatchAsync([
            Message("existing") with { MessageId = "new-source" },
            Message("fresh") with { MessageId = "new-source" },
            Message("same-text") with { MessageId = "" }
        ], new());

        Assert.Equal(MessageAppendDisposition.Duplicate, results[0].Disposition);
        Assert.Equal(MessageAppendDisposition.Appended, results[1].Disposition);
        Assert.Equal(MessageAppendDisposition.Appended, results[2].Disposition);
        Assert.Equal(3, (await store.LoadPageAsync("room", MessageKind.Danmu, 0, 10)).Count);
    }

    /// <summary>无新增数量的累计礼物仍提交来源去重，避免之后相同事件被作为增量重放。</summary>
    [Fact]
    public async Task UnchangedComboStillCommitsItsAcceptedSourceEvent()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var gift = Message("gift") with
        {
            Kind = MessageKind.Gift, ComboId = "combo", GiftId = "gift-id", GiftName = "gift",
            Quantity = 4, BatchComboCount = 4, UnitPrice = 10, TotalPrice = 40
        };
        await store.AppendAsync(gift, new());

        var results = await store.AppendBatchAsync([
            gift with { Id = "old-count", MessageId = "old-event", Quantity = 2, BatchComboCount = 2 },
            gift with { Id = "replayed", MessageId = "old-event", Quantity = 3, BatchComboCount = 0 }
        ], new());

        Assert.All(results, result => Assert.Equal(MessageAppendDisposition.Duplicate, result.Disposition));
        Assert.Equal(4, Assert.Single(await store.LoadPageAsync("room", MessageKind.Gift, 0, 10)).Quantity);
    }

    /// <summary>队列尚未执行的取消批次不会登记消息或来源 ID。</summary>
    [Fact]
    public async Task CanceledBatchDoesNotWrite()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.AppendBatchAsync([Message("canceled")], new(), cancellationToken: cancellation.Token));

        Assert.Empty(await store.LoadPageAsync("room", MessageKind.Danmu, 0, 10));
        Assert.Equal(0, store.AppendTransactionCount);
    }

    /// <summary>批次长度受到存储边界统一约束。</summary>
    [Fact]
    public async Task BatchCountMustBeWithinTheSharedLimit()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AppendBatchAsync([], new()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.AppendBatchAsync(
            Enumerable.Range(0, IMessageStore.MAX_BATCH_COUNT + 1).Select(index => Message(index.ToString())).ToArray(), new()));
    }

    private static MessageRecord Message(string id)
    {
        return new()
        {
            Id = id, RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu,
            MessageId = "source-" + id, Content = "合法的同文弹幕", UserName = "viewer"
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
