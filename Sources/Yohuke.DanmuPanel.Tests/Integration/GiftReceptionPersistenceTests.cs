using System.Text;
using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Reception;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证礼物协议分发后的入库数量、金额与跨重启幂等性。</summary>
public sealed class GiftReceptionPersistenceTests
{
    /// <summary>同事务的多种礼物均入库，整批重放不吞种类、不重复增加数量。</summary>
    [Fact]
    public async Task V2BatchPersistsEveryGiftAndRemainsIdempotentAfterRestart()
    {
        using var directory = new MessageStoreTestDirectory();
        using var socket = new BilibiliLiveWebSocket(100, "");
        var pending = new List<Task<MessageAppendResult>>();
        var body = Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateBatch().ToString());

        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            socket.GiftReceived += (_, message) => pending.Add(store.AppendAsync(RoomMessageMapper.Map(message, "tab", 0), new()));
            socket.AnalyseBusinessPackage(body);
            await Task.WhenAll(pending);
        }

        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        using var replaySocket = new BilibiliLiveWebSocket(100, "");
        pending.Clear();
        replaySocket.GiftReceived += (_, message) => pending.Add(reopened.AppendAsync(RoomMessageMapper.Map(message, "tab", 0), new()));
        replaySocket.AnalyseBusinessPackage(body);
        var replayed = await Task.WhenAll(pending);

        Assert.Equal(3, replayed.Length);
        Assert.All(replayed, result => Assert.Equal(MessageAppendDisposition.Duplicate, result.Disposition));
        var records = await reopened.LoadPageAsync("tab", MessageKind.Gift, 0, 20);
        Assert.Equal(3, records.Count);
        Assert.Equal(10, records.Sum(record => record.Quantity));
        Assert.Equal(111m, records.Sum(record => record.TotalPrice));
        Assert.Equal(4, records.Single(record => record.GiftId == "32128").Quantity);
        Assert.Equal(1, records.Single(record => record.GiftId == "32125").Quantity);
        Assert.Equal(5, records.Single(record => record.GiftId == "32126").Quantity);
    }

    /// <summary>同用户同礼物同连击的金币与银瓜子事件分别保存，不混用付费单价。</summary>
    [Fact]
    public async Task PaidAndFreeGiftsDoNotShareComboAmounts()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var source = new BilibiliGiftMessage
        {
            MessageId = "paid", RoomId = "100", Uid = 42, UserName = "测试用户",
            GiftId = "34001", GiftName = "粉丝团灯牌", Quantity = 1,
            BatchComboId = "same-combo", CoinType = "gold", UnitPrice = 100
        };
        var options = new MessageReadOptions { GiftAutoPinMinimum = 0.1m };
        await store.AppendAsync(RoomMessageMapper.Map(source, "tab", 0), options);
        source.MessageId = "free";
        source.CoinType = "silver";
        source.UnitPrice = 1000;
        await store.AppendAsync(RoomMessageMapper.Map(source, "tab", 0), options);

        var feed = await store.LoadFeedAsync("tab", MessageKind.Gift, 20, null);

        Assert.Equal(2, feed.Records.Count);
        var paid = Assert.Single(feed.Records, record => record.GiftCoinType == "gold");
        var free = Assert.Single(feed.Records, record => record.GiftCoinType == "silver");
        Assert.Equal(0.1m, paid.TotalPrice);
        Assert.True(paid.IsPriority);
        Assert.Equal(0m, free.TotalPrice);
        Assert.Equal(0m, free.UnitPrice);
        Assert.False(free.IsPriority);
        Assert.Equal(1, free.Quantity);
    }

    /// <summary>单条礼物解析错误仅报告失败通知，监听会话仍能将后续整批礼物排空入库。</summary>
    [AvaloniaFact]
    public async Task RecoverableGiftParseErrorDoesNotStopReceptionSession()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var store = new SqliteMessageStore(directory.DatabasePath);
        var receiver = new ReceptionTestSocket();
        var session = new RoomReceptionSession(new RoomTabState { Id = "tab", RoomId = "100" }, "", store,
            () => new PanelSettings(), new ReceptionTestSocketFactory(receiver), new ReceptionTestHistory());
        using var parser = new BilibiliLiveWebSocket(100, "");
        parser.GiftReceived += (_, message) => receiver.Emit(message);
        parser.ErrorReceived += (_, error) => receiver.Error(error);
        var errors = 0;
        session.Failed += _ => errors++;
        await session.StartAsync();

        parser.AnalyseBusinessPackage(Encoding.UTF8.GetBytes("""{"cmd":"SEND_GIFT_V2","data":{"pb":"UgU="}}"""));
        parser.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateBatch().ToString()));
        await session.DisposeAsync();

        Assert.Equal(1, errors);
        Assert.False(session.HasFailed);
        Assert.Equal(3, (await store.LoadPageAsync("tab", MessageKind.Gift, 0, 20)).Count);
    }
}
