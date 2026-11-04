using System.Text;
using Newtonsoft.Json.Linq;
using Xunit;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;

namespace Yohuke.DanmuPanel.Tests.Reception;

/// <summary>验证 SC 通知的命令边界、金额校验和业务事件分发。</summary>
public class BilibiliSuperChatParserTests
{
    /// <summary>验证删除通知不会生成空白的零元 SC。</summary>
    [Theory]
    [InlineData("SUPER_CHAT_MESSAGE_DELETE")]
    [InlineData("SUPER_CHAT_MESSAGE_DELETE:1")]
    [InlineData("SUPER_CHAT_MESSAGE_JPN_DELETE")]
    [InlineData("SUPER_CHAT_MESSAGE_JPN_DELETE:1")]
    public void DeletionNotificationsDoNotPublishSuperChat(string command)
    {
        var json = JObject.Parse("""
            {"data":{"ids":[987654321]}}
            """);
        json["cmd"] = command;
        using var socket = new BilibiliLiveWebSocket(200, "");
        var messages = new List<BilibiliSuperChatMessage>();
        socket.SuperChatReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));

        Assert.Empty(messages);
        Assert.Null(BilibiliLiveWebSocket.ParseSuperChatMessage("200", json));
    }

    /// <summary>验证删除命令即使附带完整留言字段也不会被当作新留言。</summary>
    [Theory]
    [InlineData("SUPER_CHAT_MESSAGE_DELETE")]
    [InlineData("SUPER_CHAT_MESSAGE_JPN_DELETE")]
    public void DeletionCommandsAreRejectedBeforeReadingMessageFields(string command)
    {
        var json = CreateMessage(command);

        Assert.Null(BilibiliLiveWebSocket.ParseSuperChatMessage("200", json));
    }

    /// <summary>验证缺失、零值、负值和无效金额均不会触发 SC 事件。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("\"invalid\"")]
    public void InvalidPricesDoNotPublishSuperChat(string? priceJson)
    {
        var json = CreateMessage("SUPER_CHAT_MESSAGE");
        var data = (JObject)json["data"]!;

        if (priceJson is null)
        {
            data.Remove("price");
        }
        else
        {
            data["price"] = JToken.Parse(priceJson);
        }

        using var socket = new BilibiliLiveWebSocket(200, "");
        var messages = new List<BilibiliSuperChatMessage>();
        socket.SuperChatReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));

        Assert.Empty(messages);
        Assert.Null(BilibiliLiveWebSocket.ParseSuperChatMessage("200", json));
    }

    /// <summary>验证普通版、日文版及冒号后缀消息在删除通知前后仍能正常分发。</summary>
    [Theory]
    [InlineData("SUPER_CHAT_MESSAGE")]
    [InlineData("SUPER_CHAT_MESSAGE:1")]
    [InlineData("SUPER_CHAT_MESSAGE_JPN")]
    [InlineData("SUPER_CHAT_MESSAGE_JPN:1")]
    public void ValidMessagesContinueAfterDeletionNotification(string command)
    {
        var json = CreateMessage(command);
        using var socket = new BilibiliLiveWebSocket(200, "");
        var messages = new List<BilibiliSuperChatMessage>();
        socket.SuperChatReceived += (_, message) => messages.Add(message);

        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));
        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes("""
            {"cmd":"SUPER_CHAT_MESSAGE_DELETE","data":{"ids":[987654321]}}
            """));
        json["data"]!["id"] = 987654322;
        socket.AnalyseBusinessPackage(Encoding.UTF8.GetBytes(json.ToString()));

        Assert.Equal(2, messages.Count);
        Assert.Equal("987654321", messages[0].MessageId);
        Assert.Equal("987654322", messages[1].MessageId);
        Assert.All(messages, message =>
        {
            Assert.Equal("200", message.RoomId);
            Assert.Equal(42, message.Uid);
            Assert.Equal("用户B", message.UserName);
            Assert.Equal("SC内容", message.Content);
            Assert.Equal(30, message.Price);
            Assert.Equal("￥30", message.PriceText);
            Assert.Equal(1800000000, message.Timestamp);
        });
    }

    private static JObject CreateMessage(string command)
    {
        var json = JObject.Parse("""
            {
              "data": {
                "id": 987654321,
                "uid": 42,
                "message": "SC内容",
                "price": 30,
                "price_text": "￥30",
                "ts": 1800000000,
                "user_info": {"uname":"用户B"}
              }
            }
            """);
        json["cmd"] = command;
        return json;
    }
}
