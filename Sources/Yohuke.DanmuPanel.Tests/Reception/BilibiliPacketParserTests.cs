using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Avalonia.Headless.XUnit;
using Newtonsoft.Json.Linq;
using Xunit;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;

namespace Yohuke.DanmuPanel.Tests.Reception;

/// <summary>验证协议包拆分、压缩与单消息错误隔离。</summary>
public sealed class BilibiliPacketParserTests
{
    /// <summary>心跳包不会吞掉同一 WebSocket 消息内的后续礼物包。</summary>
    [Fact]
    public void HeartbeatsBetweenGiftPacketsDoNotDiscardMessages()
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);
        var heartbeat = Packet([], 1, 3);
        var gift = Packet(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateLegacy().ToString()));

        socket.AnalysePackage([.. heartbeat, .. gift, .. heartbeat, .. gift]);

        Assert.Equal(2, messages.Count);
    }

    /// <summary>Brotli 与 zlib 压缩的多消息包均完整分发。</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void CompressedPacketsDispatchEveryGift(int version)
    {
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        socket.GiftReceived += (_, message) => messages.Add(message);
        var gift = Packet(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateBatch().ToString()));
        var payload = Compress([.. Packet([], 1, 3), .. gift], version);

        socket.AnalysePackage(Packet(payload, version));

        Assert.Equal(3, messages.Count);
        Assert.Equal(10, messages.Sum(message => message.Quantity));
    }

    /// <summary>损坏 JSON、Protobuf 或压缩内容只影响当前业务包。</summary>
    [AvaloniaTheory]
    [InlineData("json")]
    [InlineData("command")]
    [InlineData("protobuf")]
    [InlineData("compression")]
    public void MalformedPacketDoesNotDiscardFollowingPacket(string kind)
    {
        var broken = kind switch
        {
            "json" => Packet(Encoding.UTF8.GetBytes("{invalid")),
            "command" => Packet(Encoding.UTF8.GetBytes("""{"cmd":{}}""")),
            "protobuf" => Packet(Encoding.UTF8.GetBytes("""{"cmd":"SEND_GIFT_V2","data":{"pb":"UgU="}}""")),
            _ => Packet([255, 255, 255], 3)
        };
        var valid = Packet(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateLegacy().ToString()));
        using var socket = new BilibiliLiveWebSocket(100, "");
        var messages = new List<BilibiliGiftMessage>();
        var errors = new List<Exception>();
        socket.GiftReceived += (_, message) => messages.Add(message);
        socket.ErrorReceived += (_, error) => errors.Add(error);

        socket.AnalysePackage([.. broken, .. valid]);

        Assert.Single(errors);
        Assert.Single(messages);
    }

    /// <summary>按协议声明的头长度定位业务体，不将扩展头当作 JSON。</summary>
    [Fact]
    public void ExtendedHeaderLengthIsRespected()
    {
        var packet = Packet(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateLegacy().ToString()), headerLength: 20);
        using var socket = new BilibiliLiveWebSocket(100, "");
        var count = 0;
        socket.GiftReceived += (_, _) => count++;

        socket.AnalysePackage(packet);

        Assert.Equal(1, count);
    }

    /// <summary>SC 可选字段形状错误时，不中断同一帧内后续礼物的解析。</summary>
    [AvaloniaTheory]
    [InlineData("user_info", "[]")]
    [InlineData("price_text", "{}")]
    [InlineData("ts", "\"invalid\"")]
    [InlineData("background_color_start", "{}")]
    public void MalformedSuperChatFieldsDoNotDiscardFollowingGift(string field, string value)
    {
        var json = JObject.Parse("""
            {"cmd":"SUPER_CHAT_MESSAGE","data":{"id":1,"uid":42,"price":30,
            "message":"测试SC","ts":1800000000,"user_info":{"uname":"测试用户"}}}
            """);
        json["data"]![field] = JToken.Parse(value);
        var malformed = Packet(Encoding.UTF8.GetBytes(json.ToString()));
        var gift = Packet(Encoding.UTF8.GetBytes(BilibiliGiftTestData.CreateLegacy().ToString()));
        using var socket = new BilibiliLiveWebSocket(100, "");
        var count = 0;
        socket.GiftReceived += (_, _) => count++;

        socket.AnalysePackage([.. malformed, .. gift]);

        Assert.Equal(1, count);
    }

    /// <summary>非法长度明确报错并终止当前缓冲区，不越界或循环重试。</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(-1)]
    public void InvalidPacketLengthIsReported(int length)
    {
        var packet = Packet([]);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), length);
        using var socket = new BilibiliLiveWebSocket(100, "");
        var errors = new List<Exception>();
        socket.ErrorReceived += (_, error) => errors.Add(error);

        socket.AnalysePackage(packet);

        Assert.Single(errors);
    }

    private static byte[] Packet(byte[] body, int version = 1, int operation = 5, int headerLength = 16)
    {
        var packet = new byte[headerLength + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), (ushort)headerLength);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), (ushort)version);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8, 4), operation);
        body.CopyTo(packet, headerLength);
        return packet;
    }

    private static byte[] Compress(byte[] body, int version)
    {
        using var output = new MemoryStream();
        using (Stream compressor = version == 3
                   ? new BrotliStream(output, CompressionMode.Compress, true)
                   : new ZLibStream(output, CompressionMode.Compress, true))
        {
            compressor.Write(body);
        }
        return output.ToArray();
    }
}
