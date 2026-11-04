using Newtonsoft.Json;
using Xunit;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Reception;

/// <summary>验证历史弹幕的可选粉丝牌和表情信息解析。</summary>
public sealed class BilibiliDanmuHistoryServiceTests
{
    /// <summary>非对象粉丝牌表示没有可读取的牌子，不影响消息主体。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("\"\"")]
    [InlineData("[]")]
    public void NonObjectMedalPreservesMessage(string medalJson)
    {
        var item = JsonConvert.DeserializeObject<BilibiliDanmuHistoryItem>($$"""
            {
              "id": "message-1", "uid": 42, "nickname": "用户A", "text": "你好",
              "timestamp": 1800000000, "user": { "medal": {{medalJson}} }
            }
            """)!;

        var message = BilibiliDanmuHistoryService.ParseItem("100", item);

        Assert.Equal("100", message.RoomId);
        Assert.Equal("message-1", message.MessageId);
        Assert.Equal(42, message.Uid);
        Assert.Equal("用户A", message.UserName);
        Assert.Equal("你好", message.Content);
        Assert.Equal(1800000000, message.Timestamp);
        Assert.Empty(message.MedalName);
        Assert.Equal(0, message.MedalLevel);
        Assert.Equal(0, message.MedalGuardLevel);
    }

    /// <summary>忽略非对象表情条目，继续读取同一消息中的有效表情。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("\"\"")]
    [InlineData("[]")]
    public void NonObjectEmoticonMappingPreservesValidMappings(string mappingJson)
    {
        var item = JsonConvert.DeserializeObject<BilibiliDanmuHistoryItem>($$"""
            {
              "text": "你好[笑]",
              "emots": {
                "[缺失]": {{mappingJson}},
                "[笑]": { "url": "https://example.invalid/smile.png" }
              }
            }
            """)!;

        var message = BilibiliDanmuHistoryService.ParseItem("100", item);

        Assert.Equal("你好[笑]", message.Content);
        var emoticon = Assert.Single(message.Emoticons);
        Assert.Equal("[笑]", emoticon.Text);
        Assert.Equal("https://example.invalid/smile.png", emoticon.Url);
    }

    /// <summary>正常粉丝牌与整条表情的信息完整保留。</summary>
    [Fact]
    public void ObjectMedalAndEmoticonPreserveMetadata()
    {
        var item = JsonConvert.DeserializeObject<BilibiliDanmuHistoryItem>("""
            {
              "text": "[笑]", "isadmin": 1,
              "user": { "medal": { "name": "粉丝牌", "level": 20, "guard_level": 3 } },
              "emoticon": { "text": "[笑]", "url": "https://example.invalid/smile.png" }
            }
            """)!;

        var message = BilibiliDanmuHistoryService.ParseItem("100", item);

        Assert.Equal("粉丝牌", message.MedalName);
        Assert.Equal(20, message.MedalLevel);
        Assert.Equal(3, message.MedalGuardLevel);
        Assert.True(message.IsRoomAdmin);
        Assert.True(message.IsEmoticon);
        var emoticon = Assert.Single(message.Emoticons);
        Assert.Equal("[笑]", emoticon.Text);
        Assert.Equal("https://example.invalid/smile.png", emoticon.Url);
    }

    /// <summary>未提供可选信息的普通弹幕正常解析。</summary>
    [Fact]
    public void MissingOptionalMetadataPreservesPlainText()
    {
        var item = JsonConvert.DeserializeObject<BilibiliDanmuHistoryItem>("""
            { "text": "你好" }
            """)!;

        var message = BilibiliDanmuHistoryService.ParseItem("100", item);

        Assert.Equal("你好", message.Content);
        Assert.Empty(message.MedalName);
        Assert.False(message.IsEmoticon);
        Assert.Empty(message.Emoticons);
    }
}
