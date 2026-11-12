using System;
using Xunit;
using Yohuke.DanmuCore.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证接收协议映射保留展示与去重字段。</summary>
public sealed class RoomMessageMapperTests
{
    /// <summary>共享协议保留全部币种，产品只将金币计入人民币金额。</summary>
    [Theory]
    [InlineData("gold", "0.1")]
    [InlineData("silver", "0")]
    [InlineData("unknown", "0")]
    [InlineData("", "0")]
    public void GiftCurrencyPolicyRemainsInPanel(string coinType, string expectedPrice)
    {
        var record = RoomMessageMapper.Map(new BilibiliGiftMessage
        {
            CoinType = coinType, UnitPrice = 100, Quantity = 3
        }, "tab", 0);
        var expected = decimal.Parse(expectedPrice, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, record.UnitPrice);
        Assert.Equal(expected * 3, record.TotalPrice);
        Assert.Equal(coinType, record.GiftCoinType);
    }

    /// <summary>协议缺少有效颜色时，Panel 继续采用原金额档位配色。</summary>
    [Theory]
    [InlineData(2, "#8B9BA8")]
    [InlineData(30, "#3171D2")]
    [InlineData(50, "#427D9E")]
    [InlineData(100, "#E2B52B")]
    [InlineData(500, "#E09443")]
    [InlineData(1000, "#E54D4D")]
    [InlineData(2000, "#AB1A32")]
    public void SuperChatWithoutProtocolColorUsesPanelPriceTier(decimal price, string expected)
    {
        var record = RoomMessageMapper.Map(new BilibiliSuperChatMessage
        {
            Price = price, BorderColor = ""
        }, "tab", 0);

        Assert.Equal(expected, record.BorderColor);
    }

    /// <summary>弹幕保留房间标签、代次、表情、粉丝牌与历史来源。</summary>
    [Fact]
    public void DanmuPreservesIdentityEmoticonsAndMedal()
    {
        var source = new BilibiliDanmuMessage
        {
            RoomId = "123", MessageId = "message", Timestamp = 1700000000, Uid = 88,
            UserName = "viewer", Content = "你好[笑]", MedalName = "粉丝牌", MedalLevel = 20, MedalGuardLevel = 3,
            RoomGuardLevel = 2, IsRoomAdmin = true,
            Emoticons = [new() { Text = "[笑]", Url = "https://example.invalid/emoticon.png" }]
        };
        var record = RoomMessageMapper.Map(source, "tab", 9, true);

        Assert.Equal("tab", record.RoomTabId);
        Assert.Equal(9, record.Generation);
        Assert.True(record.IsHistory);
        Assert.Equal(source.MessageId, record.MessageId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(source.Timestamp), record.Time);
        Assert.Equal("[笑]", Assert.Single(record.Emoticons).Text);
        Assert.Equal(source.MedalName, record.MedalName);
        Assert.Equal(source.MedalLevel, record.MedalLevel);
        Assert.Equal(source.MedalGuardLevel, record.MedalGuardLevel);
        Assert.Equal(source.RoomGuardLevel, record.RoomGuardLevel);
        Assert.True(record.IsRoomAdmin);
    }

    /// <summary>礼物价格从千分元转为元，连击 ID 与累计量独立保留。</summary>
    [Fact]
    public void GiftConvertsCurrencyWithoutLosingComboIdentity()
    {
        var source = new BilibiliGiftMessage
        {
            RoomId = "123", MessageId = "tid", Uid = 88, GiftId = "gift", GiftName = "花",
            Quantity = 3, UnitPrice = 1250, BatchComboId = "combo", BatchComboCount = 8
        };
        var record = RoomMessageMapper.Map(source, "tab", 3);

        Assert.Equal(MessageKind.Gift, record.Kind);
        Assert.Equal(1.25m, record.UnitPrice);
        Assert.Equal(3.75m, record.TotalPrice);
        Assert.Equal(8, record.BatchComboCount);
        Assert.Equal("combo", record.ComboId);
        Assert.Equal("tid", record.MessageId);
    }

    /// <summary>SC 与上舰分别进入独立分类，保留去重与颜色信息。</summary>
    [Fact]
    public void PaidMessagesKeepSeparateKinds()
    {
        var sc = RoomMessageMapper.Map(new BilibiliSuperChatMessage
        {
            RoomId = "123", MessageId = "sc", Content = "支持", Price = 30, BorderColor = "#123456"
        }, "tab", 0);
        var guard = RoomMessageMapper.Map(new BilibiliGuardPurchaseMessage
        {
            RoomId = "123", MessageId = "guard", GuardLevel = 2, Uid = 88, Quantity = 2
        }, "tab", 0);

        Assert.Equal(MessageKind.SuperChat, sc.Kind);
        Assert.Equal(30, sc.TotalPrice);
        Assert.Equal("#123456", sc.BorderColor);
        Assert.Equal(MessageKind.Guard, guard.Kind);
        Assert.Equal(2, guard.GuardLevel);
        Assert.Equal(2, guard.Quantity);
        Assert.Equal("guard:123:id:guard", guard.MessageId);
    }
}
