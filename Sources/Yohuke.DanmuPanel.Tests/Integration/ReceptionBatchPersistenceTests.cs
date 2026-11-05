using System.Collections.Concurrent;
using Xunit;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证真实接收会话将积压消息有界合批，并仅在提交后通知观察者。</summary>
public sealed class ReceptionBatchPersistenceTests
{
    /// <summary>首条写入被阻塞时不发布消息，释放后按一条、六十四条、六十四条、两条排空。</summary>
    [Fact]
    public async Task BacklogIsCommittedInBoundedBatchesBeforeItIsPublished()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var blocked = new BlockingReceptionStore(store);
        var socket = new ReceptionTestSocket();
        var published = new ConcurrentQueue<string>();
        var session = new RoomReceptionSession(new() { Id = "room", RoomId = "123" }, "", blocked,
            () => new PanelSettings(), new ReceptionTestSocketFactory(socket), new ReceptionTestHistory(), 256);
        session.Committed += record => published.Enqueue(record.MessageId);
        await session.StartAsync();
        socket.Emit(Message(0));
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 1; index <= 130; index++)
        {
            socket.Emit(Message(index));
        }

        Assert.Empty(published);
        Assert.Equal(0, store.AppendTransactionCount);
        blocked.Release.TrySetResult();
        await session.DisposeAsync();

        Assert.False(session.HasFailed);
        Assert.Equal(4, store.AppendTransactionCount);
        Assert.Equal(Enumerable.Range(0, 131).Select(index => "event-" + index), published);
        Assert.Equal(131, (await store.LoadPageAsync("room", MessageKind.Danmu, 0, 200)).Count);
    }

    private static BilibiliDanmuMessage Message(int index)
    {
        return new()
        {
            RoomId = "123", MessageId = "event-" + index, Uid = 42,
            UserName = "viewer", Content = "连续弹幕"
        };
    }
}
