using System.Text;
using Avalonia.Headless.XUnit;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Xunit;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Apis.Protocols.Bilibili;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Reception;

/// <summary>验证礼物新旧协议、币种、批量分发与错误隔离。</summary>
public sealed class BilibiliGiftParserTests
{
    /// <summary>付费与免费礼物都可接收，只有金币单价换算为人民币。</summary>
    [Theory]
    [InlineData("gold", "0.1")]
    [InlineData("silver", "0")]
    [InlineData("unknown", "0")]
    [InlineData("", "0")]
    public void LegacyGiftPublishesWithCorrectMonetaryValue(string coinType, string expectedPrice)
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateLegacy(coinType).ToString()));

        var message = Assert.Single(messages);
        Assert.Equal("粉丝团灯牌", message.GiftName);
        Assert.Equal(1, message.Quantity);
        var record = RoomMessageMapper.Map(message, "tab", 0);
        Assert.Equal(decimal.Parse(expectedPrice, System.Globalization.CultureInfo.InvariantCulture), record.UnitPrice);
        Assert.Equal(record.UnitPrice, record.TotalPrice);
    }

    /// <summary>V2 中同一事务的每种礼物独立分发，整批数量不覆盖单种数量。</summary>
    [Theory]
    [InlineData("SEND_GIFT_V2")]
    [InlineData("SEND_GIFT_V2:1")]
    public void V2BatchPublishesEachGiftWithDistinctStableIdentity(string command)
    {
        var json = BilibiliGiftTestData.CreateBatch();
        json["cmd"] = command;
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));

        Assert.Equal(new[] { "32128", "32125", "32126" }, messages.Select(message => message.GiftId));
        Assert.Equal(new[] { 4, 1, 5 }, messages.Select(message => message.Quantity));
        Assert.Equal(3, messages.Select(message => message.MessageId).Distinct().Count());
        Assert.All(messages, message =>
        {
            Assert.Equal(42, message.Uid);
            Assert.Equal("测试用户", message.UserName);
            Assert.Equal("test-ten-draw-combo", message.BatchComboId);
            Assert.Equal(0, message.BatchComboCount);
            Assert.Equal(1700000000, message.Timestamp);
        });
        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));
        Assert.Equal(messages.Take(3).Select(message => message.MessageId),
            messages.Skip(3).Select(message => message.MessageId));
    }

    /// <summary>未知礼物变体不会因为前缀相同而误当成旧版礼物。</summary>
    [Fact]
    public void UnknownGiftCommandDoesNotPublish()
    {
        var json = BilibiliGiftTestData.CreateLegacy();
        json["cmd"] = "SEND_GIFT_FUTURE";
        using var socket = new BilibiliLiveWebSocket(100, "");
        var count = 0;
        socket.GiftReceived += (_, _) => count++;

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));

        Assert.Equal(0, count);
    }

    /// <summary>损坏的已知礼物消息产生诊断，后续消息继续接收。</summary>
    [AvaloniaTheory]
    [InlineData("invalid-base64!")]
    [InlineData("UgU=")]
    [InlineData("")]
    public void MalformedV2IsReportedWithoutInterruptingFollowingGift(string encoded)
    {
        var json = BilibiliGiftTestData.CreateBatch();
        json["data"]!["pb"] = encoded;
        using var socket = new BilibiliLiveWebSocket(100, "");
        var errors = new List<Exception>();
        var messages = new List<BilibiliGiftMessage>();
        socket.ErrorReceived += (_, error) => errors.Add(error);
        socket.GiftReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));
        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateLegacy().ToString()));

        Assert.Single(errors);
        Assert.Single(messages);
        Assert.DoesNotContain(encoded.Length > 0 ? encoded : "never-written-payload", errors[0].Message);
    }

    /// <summary>V2 的金币、银瓜子和未知币种保留原始币种，只对金币计算人民币。</summary>
    [Theory]
    [InlineData("gold", "0.1")]
    [InlineData("silver", "0")]
    [InlineData("future", "0")]
    [InlineData("", "0")]
    public void V2SingleGiftPreservesCurrencyAndOneBatteryValue(string coinType, string expectedPrice)
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);
        var json = BilibiliGiftTestData.Encode(BilibiliGiftTestData.CreateSingleBroadcast(coinType));

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));

        var message = Assert.Single(messages);
        Assert.Equal(coinType, message.CoinType);
        Assert.Equal("single-transaction", message.TransactionId);
        var record = RoomMessageMapper.Map(message, "tab", 0);
        Assert.Equal(coinType, record.GiftCoinType);
        Assert.Equal(decimal.Parse(expectedPrice, System.Globalization.CultureInfo.InvariantCulture), record.TotalPrice);
    }

    /// <summary>新用户结构优先于旧字段，并保留超过 32 位的 UID 及毫秒服务器时间。</summary>
    [Fact]
    public void V2PrefersStructuredSenderAndNormalizesMilliseconds()
    {
        var broadcast = BilibiliGiftTestData.CreateSingleBroadcast();
        broadcast.SenderUinfo = new UserInfo { Uid = 4294967342, Base = new UserBase { Name = "新用户" } };
        broadcast.GiftList[0].Timestamp = 1800000000123;
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(BilibiliGiftTestData.Encode(broadcast).ToString()));

        var message = Assert.Single(messages);
        Assert.Equal(4294967342, message.Uid);
        Assert.Equal("新用户", message.UserName);
        Assert.Equal(1800000000, message.Timestamp);
    }

    /// <summary>同事务同礼物的重复条目有独立身份，重放时身份保持一致。</summary>
    [Fact]
    public void RepeatedGiftIdsWithinTransactionKeepSeparateStableIdentities()
    {
        var broadcast = BilibiliGiftTestData.CreateSingleBroadcast();
        broadcast.GiftList.Add(broadcast.GiftList[0].Clone());
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);
        var body = Encoding.UTF8.GetBytes(BilibiliGiftTestData.Encode(broadcast).ToString());

        socket.AnalyseBusinessPackage(body);
        socket.AnalyseBusinessPackage(body);

        Assert.Equal(4, messages.Count);
        Assert.NotEqual(messages[0].MessageId, messages[1].MessageId);
        Assert.Equal(messages[0].MessageId, messages[2].MessageId);
        Assert.Equal(messages[1].MessageId, messages[3].MessageId);
    }

    /// <summary>无效数量、超界 UID 和空礼物列表在发布前拒绝整个批次。</summary>
    [AvaloniaTheory]
    [InlineData("zero-quantity")]
    [InlineData("overflow-quantity")]
    [InlineData("overflow-uid")]
    [InlineData("empty-list")]
    [InlineData("empty-name")]
    public void InvalidV2BatchDoesNotPublishPartialResults(string invalidField)
    {
        var broadcast = BilibiliGiftTestData.CreateSingleBroadcast();
        broadcast.GiftList.Add(broadcast.GiftList[0].Clone());
        switch (invalidField)
        {
            case "zero-quantity":
                broadcast.GiftList[1].Num = 0;
                break;
            case "overflow-quantity":
                broadcast.GiftList[1].Num = (ulong)int.MaxValue + 1;
                break;
            case "overflow-uid":
                broadcast.Uid = ulong.MaxValue;
                break;
            case "empty-list":
                broadcast.GiftList.Clear();
                break;
            case "empty-name":
                broadcast.GiftList[1].GiftName = "";
                break;
        }
        using var socket = new BilibiliLiveWebSocket(100, "");
        var count = 0;
        var errors = new List<Exception>();
        socket.GiftReceived += (_, _) => count++;
        socket.ErrorReceived += (_, error) => errors.Add(error);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(BilibiliGiftTestData.Encode(broadcast).ToString()));

        Assert.Equal(0, count);
        Assert.Single(errors);
    }

    /// <summary>旧协议仍优先读取结构化用户并保留原有连击累计字段。</summary>
    [Fact]
    public void LegacyStructuredSenderAndCumulativeComboRemainCompatible()
    {
        var json = BilibiliGiftTestData.CreateLegacy();
        json["cmd"] = "SEND_GIFT:1";
        var data = (JObject)json["data"]!;
        data["sender_uinfo"] = new JObject { ["uid"] = 4294967342, ["base"] = new JObject { ["name"] = "新用户" } };
        data["batch_combo_send"] = new JObject { ["batch_combo_num"] = 8, ["batch_combo_id"] = "legacy-combo" };

        var message = BilibiliLiveWebSocket.ParseGiftMessage("100", json);

        Assert.NotNull(message);
        Assert.Equal(4294967342, message.Uid);
        Assert.Equal("新用户", message.UserName);
        Assert.Equal("legacy-combo", message.BatchComboId);
        Assert.Equal(8, message.BatchComboCount);
        Assert.Equal("paid-gift", message.MessageId);
    }
}
