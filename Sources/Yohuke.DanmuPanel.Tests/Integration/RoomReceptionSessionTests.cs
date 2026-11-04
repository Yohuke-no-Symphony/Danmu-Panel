using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证监听与存储之间的失败隔离和排空。</summary>
public sealed class RoomReceptionSessionTests
{
    /// <summary>界面和错误观察者抛出异常时，所有已接收消息仍可提交。</summary>
    [Fact]
    public async Task ThrowingObserversDoNotInterruptPersistenceOrDispose()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var socket = new ReceptionTestSocket();
        var committed = 0;
        var session = CreateSession(store, socket);
        session.Committed += _ => throw new InvalidOperationException("UI fixture");
        session.Committed += _ => committed++;
        session.StatusChanged += _ => throw new InvalidOperationException("status fixture");
        session.Failed += _ => throw new InvalidOperationException("error fixture");
        await session.StartAsync();
        for (var index = 0; index < 30; index++)
        {
            socket.Emit(Message(index));
        }
        socket.Disconnect();
        socket.Recover();
        await session.DisposeAsync();

        Assert.False(session.HasFailed);
        Assert.Equal(30, committed);
        Assert.Equal(30, (await store.LoadPageAsync("tab", MessageKind.Danmu, 0, 100)).Count);
    }

    /// <summary>队列满停止新接收并报告终止，但排空此前已接受的消息。</summary>
    [Fact]
    public async Task QueueOverflowDrainsAlreadyAcceptedRecords()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var blocked = new BlockingReceptionStore(store);
        var socket = new ReceptionTestSocket();
        var session = CreateSession(blocked, socket, 1);
        var terminalFailures = 0;
        session.TerminalFailure += _ => terminalFailures++;
        await session.StartAsync();
        socket.Emit(Message(1));
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.Emit(Message(2));
        socket.Emit(Message(3));
        blocked.Release.TrySetResult();
        await session.DisposeAsync();

        Assert.True(session.HasFailed);
        Assert.Equal(1, terminalFailures);
        var rows = await store.LoadPageAsync("tab", MessageKind.Danmu, 0, 100);
        Assert.Equal(new[] { "message-1", "message-2" }, rows.Select(row => row.MessageId).Order().ToArray());
    }

    /// <summary>短暂网络异常和重连通知不产生永久监听失败。</summary>
    [Fact]
    public async Task NetworkErrorRemainsRecoverable()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var socket = new ReceptionTestSocket();
        var session = CreateSession(store, socket);
        var errors = 0;
        session.Failed += _ => errors++;
        await session.StartAsync();
        socket.Error(new IOException("transient fixture"));
        socket.Disconnect();
        socket.Recover();
        socket.Emit(Message(1));
        await session.DisposeAsync();

        Assert.Equal(1, errors);
        Assert.False(session.HasFailed);
        Assert.Single(await store.LoadPageAsync("tab", MessageKind.Danmu, 0, 10));
    }

    private static RoomReceptionSession CreateSession(IMessageStore store, ReceptionTestSocket socket, int capacity = 4096)
    {
        return new(new RoomTabState { Id = "tab", RoomId = "123" }, "", store,
            () => new PanelSettings(), new ReceptionTestSocketFactory(socket), new ReceptionTestHistory(), capacity);
    }

    /// <summary>跨启动补拉复用持久消息观测，不合并真实存在的多条同文弹幕。</summary>
    [Fact]
    public async Task StartupHistoryReconcilesPersistedDuplicatesOneForOne()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var original = new BilibiliDanmuMessage
        {
            RoomId = "123", Uid = 10, UserName = "viewer", Content = "同文", Timestamp = 1700000000
        };
        await store.AppendAsync(RoomMessageMapper.Map(original, "tab", 0), new());
        await store.AppendAsync(RoomMessageMapper.Map(original, "tab", 0), new());
        var newer = new BilibiliDanmuMessage
        {
            RoomId = "123", Uid = 10, UserName = "viewer", Content = "新增", Timestamp = 1700000001
        };
        var socket = new ReceptionTestSocket();
        var session = new RoomReceptionSession(new() { Id = "tab", RoomId = "123" }, "", store,
            () => new PanelSettings(), new ReceptionTestSocketFactory(socket),
            new ReceptionTestHistory { Messages = [original, original, newer] });
        await session.StartAsync();
        await session.DisposeAsync();

        var rows = await store.LoadRecentAsync("tab", MessageKind.Danmu, 10);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(row => row.Content == "同文"));
        Assert.Single(rows, row => row.Content == "新增");
    }

    private static BilibiliDanmuMessage Message(int sequence)
    {
        return new() { MessageId = "message-" + sequence, RoomId = "123", Uid = 10, UserName = "viewer", Content = "正文" };
    }
}
