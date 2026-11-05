using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

/// <summary>验证手动重译的持久化意图、旧任务隔离和消息代次边界。</summary>
public sealed class TranslationRetryTests
{
    /// <summary>重译已完成消息时清除旧译文，重启存储后仍保留强制请求标识。</summary>
    [Fact]
    public async Task RetryingCompletedTranslationPersistsTheRefreshAcrossReopening()
    {
        using var directory = new MessageStoreTestDirectory();
        var message = Message(MessageKind.Danmu);
        string refreshId;
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            await store.AppendAsync(message, new(), "configuration");
            Assert.True(await store.ApplyTranslationAsync(message.Id, message.Generation, "configuration", "旧译文"));
            Assert.False(await store.UpsertTranslationJobAsync(message.Id, message.Generation, "configuration"));

            Assert.True(await store.RetryTranslationJobAsync(message.Id, message.Generation, "configuration"));

            var pending = Assert.Single(await store.GetPendingAsync(10));
            refreshId = pending.RefreshId;
            Assert.NotEmpty(refreshId);
            Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
            Assert.True(await store.UpsertTranslationJobAsync(message.Id, message.Generation, "configuration"));
            Assert.Contains(message.Id, await store.UpsertTranslationJobsAsync([message], "configuration"));
            Assert.Equal(refreshId, Assert.Single(await store.GetPendingAsync(10)).RefreshId);
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var restored = Assert.Single(await reopened.GetPendingAsync(10));
        Assert.Equal(refreshId, restored.RefreshId);
        Assert.True(await reopened.CompleteAsync(restored, "新译文"));
        Assert.Equal("新译文", (await reopened.GetByIdAsync(message.Id))!.Translation);
    }

    /// <summary>重新点击重译后，普通任务和此前重译任务的成功或失败均不能覆盖新待办。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryRejectsPreviousCompletionAndFailure(bool previousWasRefresh)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message(MessageKind.Danmu);
        await store.AppendAsync(message, new(), "configuration");
        if (previousWasRefresh)
        {
            Assert.True(await store.RetryTranslationJobAsync(message.Id, message.Generation, "configuration"));
        }
        var previous = Assert.Single(await store.GetPendingAsync(10));

        Assert.True(await store.RetryTranslationJobAsync(message.Id, message.Generation, "configuration"));
        var current = Assert.Single(await store.GetPendingAsync(10));

        Assert.NotEqual(previous.RefreshId, current.RefreshId);
        Assert.False(await store.CompleteAsync(previous, "过期译文"));
        Assert.False(await store.FailAsync(previous, "过期错误"));
        Assert.Equal(current.RefreshId, Assert.Single(await store.GetPendingAsync(10)).RefreshId);
        Assert.Null((await store.GetByIdAsync(message.Id))!.Translation);
        Assert.True(await store.CompleteAsync(current, "新译文"));
        Assert.Equal("新译文", (await store.GetByIdAsync(message.Id))!.Translation);
    }

    /// <summary>已清空、错误代次或不支持翻译的消息不能通过重译入口生成任务。</summary>
    [Fact]
    public async Task RetryRejectsMissingStaleAndUnsupportedMessages()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var message = Message(MessageKind.Danmu);
        var gift = Message(MessageKind.Gift);
        await store.AppendAsync(message, new());
        await store.AppendAsync(gift, new());

        Assert.False(await store.RetryTranslationJobAsync("missing", 0, "configuration"));
        Assert.False(await store.RetryTranslationJobAsync(message.Id, 1, "configuration"));
        Assert.False(await store.RetryTranslationJobAsync(gift.Id, 0, "configuration"));
        await store.ClearRoomAsync(message.RoomTabId);
        Assert.False(await store.RetryTranslationJobAsync(message.Id, 0, "configuration"));
        Assert.Empty(await store.GetPendingAsync(10));
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new() { RoomTabId = "room", RoomId = "123", Kind = kind, Content = "hello" };
    }
}
