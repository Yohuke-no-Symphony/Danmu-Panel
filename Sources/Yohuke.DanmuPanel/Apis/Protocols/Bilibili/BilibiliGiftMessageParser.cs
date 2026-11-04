using System.Globalization;
using Newtonsoft.Json.Linq;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;

namespace Yohuke.DanmuPanel.Apis.Protocols.Bilibili;

internal static class BilibiliGiftMessageParser
{
    private const long MAX_UNIX_TIMESTAMP_SECONDS = 253402300799;

    internal static bool SupportsCommand(string? command)
    {
        return command?.Split(':', 2)[0] is "SEND_GIFT" or "SEND_GIFT_V2";
    }

    internal static IReadOnlyList<BilibiliGiftMessage> Parse(string roomId, JObject json)
    {
        var command = ReadText(json["cmd"]).Split(':', 2)[0];
        if (command == "SEND_GIFT_V2")
        {
            return ParseV2(roomId, json);
        }

        if (command == "SEND_GIFT")
        {
            var message = ParseLegacy(roomId, json);
            return message is not null ? [message] : throw new InvalidDataException("Gift fields are missing or invalid.");
        }

        return [];
    }

    internal static BilibiliGiftMessage? ParseLegacy(string roomId, JObject json)
    {
        if (ReadText(json["cmd"]).Split(':', 2)[0] != "SEND_GIFT" || json["data"] is not JObject data)
        {
            return null;
        }

        var sender = data["sender_uinfo"] as JObject;
        var senderBase = sender?["base"] as JObject;
        var giftInfo = data["gift_info"] as JObject;
        var combo = data["batch_combo_send"] as JObject;
        var uid = ReadInteger(sender?["uid"]);
        var userName = ReadText(senderBase?["name"]);
        var giftName = ReadText(data["giftName"]);
        var giftId = ReadText(data["giftId"]);
        var quantity = ReadCount(data["super_gift_num"]);
        var batchComboCount = ReadCount(combo?["batch_combo_num"]);

        uid = uid > 0 ? uid : ReadInteger(data["uid"]);
        userName = string.IsNullOrWhiteSpace(userName) ? ReadText(data["uname"]) : userName;
        giftName = string.IsNullOrWhiteSpace(giftName) ? ReadText(giftInfo?["gift_name"]) : giftName;
        giftId = string.IsNullOrWhiteSpace(giftId) ? ReadText(giftInfo?["gift_id"]) : giftId;
        quantity = quantity > 0 ? quantity : ReadCount(data["num"]);
        quantity = quantity > 0 ? quantity : batchComboCount;

        if (uid <= 0 || string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(giftName) || quantity <= 0)
        {
            return null;
        }

        var transactionId = ReadText(data["tid"]);
        var comboId = ReadText(data["batch_combo_id"]);
        return new()
        {
            MessageId = transactionId,
            TransactionId = transactionId,
            RoomId = roomId,
            Uid = uid,
            UserName = userName,
            GiftId = giftId,
            GiftName = giftName,
            Quantity = quantity,
            BatchComboId = string.IsNullOrWhiteSpace(comboId) ? ReadText(combo?["batch_combo_id"]) : comboId,
            BatchComboCount = batchComboCount,
            CoinType = ReadText(data["coin_type"]).Trim().ToLowerInvariant(),
            UnitPrice = ReadPrice(data["price"]),
            Timestamp = ReadTimestamp(data["timestamp"], data["ts"], json["send_time"])
        };
    }

    private static IReadOnlyList<BilibiliGiftMessage> ParseV2(string roomId, JObject json)
    {
        var encoded = ReadText((json["data"] as JObject)?["pb"]);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new InvalidDataException("Gift protobuf content is missing.");
        }

        var broadcast = GiftBroadcast.Parser.ParseFrom(Convert.FromBase64String(encoded));
        var uid = broadcast.SenderUinfo?.Uid is > 0 ? broadcast.SenderUinfo.Uid : broadcast.Uid;
        var userName = broadcast.SenderUinfo?.Base?.Name;
        userName = string.IsNullOrWhiteSpace(userName) ? broadcast.Uname : userName;
        if (uid is 0 or > long.MaxValue || string.IsNullOrWhiteSpace(userName) || broadcast.GiftList.Count == 0)
        {
            throw new InvalidDataException("Gift sender or gift list is missing or invalid.");
        }

        var messages = new List<BilibiliGiftMessage>(broadcast.GiftList.Count);
        var occurrences = new Dictionary<(string TransactionId, uint GiftId), int>();
        foreach (var gift in broadcast.GiftList)
        {
            if (string.IsNullOrWhiteSpace(gift.GiftName) || gift.Num is 0 or > int.MaxValue)
            {
                throw new InvalidDataException("Gift name or quantity is missing or invalid.");
            }

            var key = (gift.Tid, gift.GiftId);
            occurrences.TryGetValue(key, out var occurrence);
            occurrences[key] = occurrence + 1;
            messages.Add(new()
            {
                MessageId = string.IsNullOrWhiteSpace(gift.Tid)
                    ? ""
                    : FormattableString.Invariant($"gift:{gift.Tid}:{gift.GiftId}:{occurrence}"),
                TransactionId = gift.Tid,
                RoomId = roomId,
                Uid = (long)uid,
                UserName = userName,
                GiftId = gift.GiftId.ToString(CultureInfo.InvariantCulture),
                GiftName = gift.GiftName,
                Quantity = (int)gift.Num,
                BatchComboId = gift.BatchComboId,
                CoinType = gift.CoinType.Trim().ToLowerInvariant(),
                UnitPrice = gift.Price,
                Timestamp = gift.Timestamp <= long.MaxValue ? NormalizeTimestamp((long)gift.Timestamp) : 0
            });
        }

        return messages;
    }

    private static string ReadText(JToken? token)
    {
        return token is JValue value ? Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? "" : "";
    }

    private static long ReadInteger(JToken? token)
    {
        return long.TryParse(ReadText(token), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static int ReadCount(JToken? token)
    {
        var value = ReadInteger(token);
        return value is > 0 and <= int.MaxValue ? (int)value : 0;
    }

    private static decimal ReadPrice(JToken? token)
    {
        return decimal.TryParse(ReadText(token), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static long ReadTimestamp(params JToken?[] candidates)
    {
        return candidates.Select(candidate => NormalizeTimestamp(ReadInteger(candidate))).FirstOrDefault(value => value > 0);
    }

    private static long NormalizeTimestamp(long value)
    {
        value = value > MAX_UNIX_TIMESTAMP_SECONDS ? value / 1000 : value;
        return value is > 0 and <= MAX_UNIX_TIMESTAMP_SECONDS ? value : 0;
    }
}
