using Xunit;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证排队中的接收消息使用实际提交时已生效的礼物门槛。</summary>
public sealed class GiftPriorityReceptionTests
{
    /// <summary>接收线程保留旧设置快照时，排队消息仍遵循之后已提交的新门槛。</summary>
    [Theory]
    [InlineData(100, 200, false)]
    [InlineData(200, 100, true)]
    public async Task QueuedReceptionUsesCommittedThresholdInsteadOfOldSettings(int previousMinimum,
        int nextMinimum, bool expectedPriority)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var previous = new MessageReadOptions { GiftAutoPinMinimum = previousMinimum };
        await store.AppendAsync(new()
        {
            Id = "existing", RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            GiftId = "flower", GiftName = "鲜花", UnitPrice = 150, TotalPrice = 150
        }, previous);
        var blocked = new BlockingReceptionStore(store);
        var socket = new ReceptionTestSocket();
        var settings = new PanelSettings
        {
            Reading = previous,
            Translation = new() { Enabled = false }
        };
        var session = new RoomReceptionSession(new() { Id = "tab", RoomId = "123" }, "", blocked,
            () => settings, new ReceptionTestSocketFactory(socket), new ReceptionTestHistory());
        try
        {
            await session.StartAsync();
            socket.Emit(new BilibiliGiftMessage
            {
                MessageId = "received", RoomId = "123", Uid = 42, UserName = "viewer",
                GiftId = "flower", GiftName = "鲜花", Quantity = 1, CoinType = "gold", UnitPrice = 150000
            });
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await store.ReevaluateReadOptionsAsync(previous with { GiftAutoPinMinimum = nextMinimum });
        }
        finally
        {
            blocked.Release.TrySetResult();
            await session.DisposeAsync();
        }

        Assert.False(session.HasFailed);
        var snapshot = await store.LoadFeedAsync("tab", MessageKind.Gift, 100, null);
        Assert.Equal(expectedPriority ? 2 : 0, snapshot.Records.Count);
        Assert.Equal(expectedPriority ? 2 : 0, snapshot.UnreadCount);
        var records = await store.LoadPageAsync("tab", MessageKind.Gift, 0, 100);
        Assert.Equal(2, records.Count);
        Assert.All(records, record =>
        {
            Assert.Equal(expectedPriority, record.IsPriority);
            Assert.False(record.IsRead);
            Assert.Equal(150m, record.TotalPrice);
        });
    }
}
