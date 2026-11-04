using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Yohuke.DanmuPanel.Apis.Protocols.Bilibili;

namespace Yohuke.DanmuPanel.Tests.Reception;

internal static class BilibiliGiftTestData
{
    // https://github.com/Akegarasu/blivedm-go/blob/957b543d2bfa8c2ac8a7af2fb42ae73eb92443b2/message/testdata/ten_blind_gift_v2.json
    internal const string TEN_DRAW_BASE64 = "CCoSDOa1i+ivleeUqOaIt0ogCIsBEPv7ARoM5b+D5Yqo55uy55uSKgbniIblh7owmHVSYwiA+wESDOeIseW/g+aKseaelRgEKIB9OODUA0IEZ29sZEoZdGVzdC10ZW4tZHJhdy10cmFuc2FjdGlvblCA4s+qBlgKYhN0ZXN0LXRlbi1kcmF3LWNvbWJvcID0A3gFoAKAfVJeCP36ARIJ55S15b2x56WoGAEo0A84mHVCBGdvbGRKGXRlc3QtdGVuLWRyYXctdHJhbnNhY3Rpb25QgOLPqgZYCmITdGVzdC10ZW4tZHJhdy1jb21ib3DQD3gFoALQD1JgCP76ARIJ5qOJ6Iqx57OWGAUoqEY4+MkEQgRnb2xkShl0ZXN0LXRlbi1kcmF3LXRyYW5zYWN0aW9uUIDiz6oGWApiE3Rlc3QtdGVuLWRyYXctY29tYm9wyN8CeAWgAqhG";

    internal static JObject CreateLegacy(string coinType = "gold")
    {
        var json = JObject.Parse("""
            {"cmd":"SEND_GIFT","data":{"tid":"paid-gift","uid":42,"uname":"测试用户",
            "giftId":34001,"giftName":"粉丝团灯牌","num":1,"price":100,"timestamp":1800000000}}
            """);
        json["data"]!["coin_type"] = coinType;
        return json;
    }

    internal static JObject CreateBatch()
    {
        return new()
        {
            ["cmd"] = "SEND_GIFT_V2",
            ["data"] = new JObject { ["pb"] = TEN_DRAW_BASE64 }
        };
    }

    internal static GiftBroadcast CreateSingleBroadcast(string coinType = "gold")
    {
        var broadcast = new GiftBroadcast { Uid = 42, Uname = "测试用户" };
        broadcast.GiftList.Add(new GiftItem
        {
            GiftId = 34001, GiftName = "粉丝团灯牌", Num = 1, Price = 100,
            CoinType = coinType, Tid = "single-transaction", Timestamp = 1800000000
        });
        return broadcast;
    }

    internal static JObject Encode(GiftBroadcast broadcast)
    {
        return new()
        {
            ["cmd"] = "SEND_GIFT_V2",
            ["data"] = new JObject { ["pb"] = Convert.ToBase64String(broadcast.ToByteArray()) }
        };
    }
}
