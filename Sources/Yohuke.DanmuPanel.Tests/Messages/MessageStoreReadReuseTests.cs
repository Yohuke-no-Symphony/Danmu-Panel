using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证完整 Feed 读取复用未变化的解析结果，并保留外部数据库更新的一致性。</summary>
public sealed class MessageStoreReadReuseTests
{
    /// <summary>从未写过消息的新库返回空快照，第一次提交后才出现对应记录。</summary>
    [Fact]
    public async Task EmptyStoreHasNoCachedRecordsBeforeItsFirstCommit()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);

        var empty = await store.LoadFeedAsync("room", MessageKind.SuperChat, 100, null);

        Assert.Empty(empty.Records);
        Assert.Empty(empty.PendingTranslationIds);
        Assert.Equal(0, empty.UnreadCount);
        await store.AppendAsync(Message(MessageKind.SuperChat, "first"), new());
        var populated = await store.LoadFeedAsync("room", MessageKind.SuperChat, 100, null);
        Assert.Equal("first", Assert.Single(populated.Records).Id);
        Assert.Equal(1, populated.UnreadCount);
    }

    /// <summary>重新打开数据库从空缓存读取，只有同一次连接中的后续读取才复用结果。</summary>
    [Fact]
    public async Task ReopenedStoreStartsColdAndThenReusesItsOwnParsedRecords()
    {
        using var directory = new MessageStoreTestDirectory();
        MessageRecord previous;
        await using (var original = new SqliteMessageStore(directory.DatabasePath))
        {
            await original.AppendAsync(Message(MessageKind.SuperChat, "persisted"), new());
            previous = Assert.Single((await original.LoadFeedAsync("room", MessageKind.SuperChat, 100, null)).Records);
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);

        var cold = Assert.Single((await reopened.LoadFeedAsync("room", MessageKind.SuperChat, 100, null)).Records);
        var warm = Assert.Single((await reopened.LoadFeedAsync("room", MessageKind.SuperChat, 100, null)).Records);

        Assert.Equal(previous.Id, cold.Id);
        Assert.Equal(previous.Content, cold.Content);
        Assert.NotSame(previous, cold);
        Assert.Same(cold, warm);
    }

    /// <summary>高频普通消息写入不会使五千条 SC 和五千条礼物重新反序列化。</summary>
    [Fact]
    public async Task OrdinaryWritesRetainAllUnchangedPriorityRecords()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        foreach (var kind in new[] { MessageKind.SuperChat, MessageKind.Gift })
        {
            for (var batch = 0; batch < 10; batch++)
            {
                var messages = Enumerable.Range(batch * 500, 500).Select(index => Message(kind, $"{kind}-{index}") with
                {
                    IsTest = true
                }).ToArray();
                await store.AppendTestMessagesAsync(messages, new());
            }
        }
        var superChats = (await store.LoadFeedAsync("room", MessageKind.SuperChat, 100, null)).Records;
        var gifts = (await store.LoadFeedAsync("room", MessageKind.Gift, 100, null)).Records;
        Assert.Equal(5000, superChats.Count);
        Assert.Equal(5000, gifts.Count);

        for (var index = 0; index < 200; index++)
        {
            await store.AppendAsync(Message(MessageKind.Danmu, $"ordinary-{index}"), new());
        }
        var refreshedSuperChats = await store.LoadFeedAsync("room", MessageKind.SuperChat, 100, null);
        var refreshedGifts = await store.LoadFeedAsync("room", MessageKind.Gift, 100, null);

        Assert.Equal(5000, refreshedSuperChats.UnreadCount);
        Assert.Equal(5000, refreshedGifts.UnreadCount);
        Assert.Equal(superChats.Count, refreshedSuperChats.Records.Count);
        Assert.Equal(gifts.Count, refreshedGifts.Records.Count);
        for (var index = 0; index < 5000; index++)
        {
            Assert.Same(superChats[index], refreshedSuperChats.Records[index]);
            Assert.Same(gifts[index], refreshedGifts.Records[index]);
        }
    }

    /// <summary>外部连接修改正文和阅读状态后，下次读取立即返回最新内容及计数。</summary>
    [Fact]
    public async Task ExternalPayloadChangesReplaceOnlyTheChangedParsedRecord()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(MessageKind.SuperChat, "changed"), new());
        await store.AppendAsync(Message(MessageKind.SuperChat, "retained"), new());
        var before = (await store.LoadFeedAsync("room", MessageKind.SuperChat, 100, null)).Records.ToDictionary(record => record.Id);
        using var connection = Open(directory);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE message_records SET priority=0,
                payload=json_set(payload,'$.Content','edited externally','$.IsRead',json('true'),'$.IsPriority',json('false'))
            WHERE id='changed';
            """;
        command.ExecuteNonQuery();

        var after = await store.LoadFeedAsync("room", MessageKind.SuperChat, 100, null);

        Assert.Equal(1, after.UnreadCount);
        var changed = after.Records.Single(record => record.Id == "changed");
        Assert.NotSame(before["changed"], changed);
        Assert.Equal("edited externally", changed.Content);
        Assert.True(changed.IsRead);
        Assert.False(changed.IsPriority);
        Assert.Same(before["retained"], after.Records.Single(record => record.Id == "retained"));
    }

    /// <summary>外部删除及相同 ID 的新消息不会被历史缓存复活或覆盖。</summary>
    [Fact]
    public async Task ExternalDeletionAndReinsertedIdDoNotReturnOldContent()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message(MessageKind.Danmu, "reused");
        await store.AppendAsync(message, new());
        var original = Assert.Single((await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null)).Records);
        using var connection = Open(directory);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM message_records WHERE id='reused';";
        command.ExecuteNonQuery();
        Assert.Empty((await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null)).Records);

        await store.AppendAsync(message with { Content = "new content" }, new());
        var restored = Assert.Single((await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null)).Records);

        Assert.NotSame(original, restored);
        Assert.Equal("new content", restored.Content);
    }

    /// <summary>翻译任务状态独立读取，不因消息正文未变化而返回旧等待状态。</summary>
    [Fact]
    public async Task PendingStatusRemainsFreshWhenMessagePayloadDoesNotChange()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(MessageKind.Danmu, "pending"), new(), "configuration");
        var before = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null);
        Assert.Contains("pending", before.PendingTranslationIds);
        using var connection = Open(directory);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE translation_jobs SET status=1 WHERE message_id='pending';";
        command.ExecuteNonQuery();

        var after = await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null);

        Assert.Empty(after.PendingTranslationIds);
        Assert.Same(Assert.Single(before.Records), Assert.Single(after.Records));
    }

    /// <summary>相同 ID 与相同 payload 在不同数据库中也使用各自独立的解析对象。</summary>
    [Fact]
    public async Task SeparateStoresDoNotShareParsedRecords()
    {
        using var firstDirectory = new MessageStoreTestDirectory();
        using var secondDirectory = new MessageStoreTestDirectory();
        await using var first = new SqliteMessageStore(firstDirectory.DatabasePath);
        await using var second = new SqliteMessageStore(secondDirectory.DatabasePath);
        var message = Message(MessageKind.Danmu, "shared-id");
        await Task.WhenAll(first.AppendAsync(message, new()), second.AppendAsync(message, new()));

        var snapshots = await Task.WhenAll(first.LoadFeedAsync("room", MessageKind.Danmu, 100, null),
            second.LoadFeedAsync("room", MessageKind.Danmu, 100, null));

        Assert.NotSame(Assert.Single(snapshots[0].Records), Assert.Single(snapshots[1].Records));
    }

    /// <summary>复用记录中的表情集合不可由调用方修改，以免污染下一次读取。</summary>
    [Fact]
    public async Task SharedParsedRecordsExposeReadOnlyEmoticons()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(MessageKind.Danmu, "emoticon") with
        {
            Emoticons = [new() { Text = "[smile]", Url = "https://example.invalid/smile.png" }]
        }, new());
        var record = Assert.Single((await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null)).Records);
        var emoticons = Assert.IsAssignableFrom<IList<MessageEmoticon>>(record.Emoticons);

        Assert.True(emoticons.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => emoticons[0] = new() { Text = "changed" });
        var repeated = Assert.Single((await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null)).Records);
        Assert.Same(record, repeated);
        Assert.Equal("[smile]", Assert.Single(repeated.Emoticons).Text);
    }

    /// <summary>外部损坏 payload 后必须报告解析错误，不能回退返回旧缓存。</summary>
    [Fact]
    public async Task InvalidExternalPayloadCannotBeHiddenByCachedRecord()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        await store.AppendAsync(Message(MessageKind.Danmu, "corrupt"), new());
        await store.LoadFeedAsync("room", MessageKind.Danmu, 100, null);
        using var connection = Open(directory);
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX ix_message_unread;
            UPDATE message_records SET payload='{' WHERE id='corrupt';
            """;
        command.ExecuteNonQuery();

        await Assert.ThrowsAnyAsync<Exception>(() => store.LoadFeedAsync("room", MessageKind.Danmu, 100, null));
    }

    private static MessageRecord Message(MessageKind kind, string id)
    {
        return new()
        {
            Id = id, RoomTabId = "room", RoomId = "123", Kind = kind,
            Content = "unchanged", UserName = "viewer", GiftName = "gift", UnitPrice = 100, TotalPrice = 100
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
