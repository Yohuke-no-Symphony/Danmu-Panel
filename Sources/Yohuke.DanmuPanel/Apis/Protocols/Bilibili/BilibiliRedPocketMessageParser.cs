using System.Globalization;
using Newtonsoft.Json.Linq;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Apis.Protocols.Bilibili;

internal static class BilibiliRedPocketMessageParser
{
    private const long MAX_UNIX_TIMESTAMP_SECONDS = 253402300799;

    internal static bool SupportsCommand(string? command)
    {
        return command?.Split(':', 2)[0] is
            "POPULARITY_RED_POCKET_NEW" or "POPULARITY_RED_POCKET_V2_NEW" or
            "POPULARITY_RED_POCKET_START" or "POPULARITY_RED_POCKET_V2_START" or
            "POPULARITY_RED_POCKET_WINNER_LIST" or "POPULARITY_RED_POCKET_V2_WINNER_LIST";
    }

    internal static BilibiliRedPocketMessage? Parse(string roomId, JObject json)
    {
        var command = ReadText(json["cmd"]).Split(':', 2)[0];
        if (!SupportsCommand(command))
        {
            return null;
        }

        if (json["data"] is not JObject data)
        {
            throw new InvalidDataException("Red pocket data is missing or invalid.");
        }

        var message = command.EndsWith("_WINNER_LIST", StringComparison.Ordinal)
            ? ParseWinners(roomId, 0, data)
            : ParseInfo(roomId, data);
        if (command.EndsWith("_NEW", StringComparison.Ordinal))
        {
            message = message with { RedPocket = message.RedPocket with { HasDetails = false } };
        }

        return message with
        {
            Timestamp = message.Timestamp > 0 ? message.Timestamp : ReadTimestamp(json["send_time"])
        };
    }

    internal static BilibiliRedPocketMessage ParseInfo(string roomId, JObject data, long fallbackLotId = 0)
    {
        var lotId = ReadPositiveLong(data["lot_id"], data["lotId"]);
        if (lotId > 0 && fallbackLotId > 0 && lotId != fallbackLotId)
        {
            throw new InvalidDataException("Red pocket detail identity does not match the requested identity.");
        }

        lotId = lotId > 0 ? lotId : fallbackLotId;
        if (lotId <= 0)
        {
            throw new InvalidDataException("Red pocket identity is missing or invalid.");
        }

        var sender = data["sender_uinfo"] as JObject ?? data["sender_info"] as JObject;
        var senderBase = sender?["base"] as JObject;
        var awards = data["awards"] as JContainer ?? data["award_list"] as JContainer ?? data["awardList"] as JContainer;
        var currentTime = ReadTimestamp(data["current_time"]);
        var startTime = ReadTimestamp(data["start_time"]);
        return new()
        {
            RoomId = roomId,
            Timestamp = currentTime > 0 ? currentTime : startTime,
            RedPocket = new()
            {
                LotId = lotId,
                Type = ReadNullableInt(data["rp_type"], data["rpType"]),
                SenderUid = ReadPositiveLong(sender?["uid"], data["sender_uid"], data["uid"]),
                SenderName = ReadText(senderBase?["name"], data["sender_name"], data["uname"]),
                SenderAvatarUrl = ReadText(senderBase?["face"], data["sender_face"]),
                IsMystery = ReadNullableBoolean(senderBase?["is_mystery"], data["is_mystery"]),
                StartTime = startTime,
                EndTime = ReadTimestamp(data["end_time"]),
                CurrentTime = currentTime,
                Duration = ReadNullableInt(data["last_time"]),
                LotStatus = ReadNullableInt(data["lot_status"]),
                UserStatus = ReadNullableInt(data["user_status"]),
                JoinRequirement = ReadNullableInt(data["join_requirement"]),
                Danmu = ReadText(data["danmu"]),
                H5Url = ReadText(data["h5_url"]),
                Price = ReadNullableDecimal(data["price"]),
                TotalPrice = ReadNullableDecimal(data["total_price"]),
                TotalBattery = ReadNullableDecimal(data["total_battery"]),
                TotalAwardCount = ReadNullableInt(data["total_num"], data["award_num"]),
                HasDetails = true,
                Awards = ParseAwards(awards)
            }
        };
    }

    internal static BilibiliRedPocketMessage ParseWinners(string roomId, long lotId, JObject data)
    {
        var responseLotId = ReadPositiveLong(data["lot_id"], data["lotId"]);
        if (responseLotId > 0 && lotId > 0 && responseLotId != lotId)
        {
            throw new InvalidDataException("Red pocket result identity does not match the requested identity.");
        }

        lotId = responseLotId > 0 ? responseLotId : lotId;
        if (lotId <= 0)
        {
            throw new InvalidDataException("Red pocket result identity is missing or invalid.");
        }

        var lotInfo = data["lot_info"] as JObject ?? data["lotInfo"] as JObject;
        var details = ParseInfo(roomId, lotInfo ?? data, lotId);
        var type = ReadNullableInt(data["rp_type"], data["rpType"], lotInfo?["rp_type"], lotInfo?["rpType"]);
        var winnerList = data["winner_info"] as JArray ?? data["winner_list"] as JArray ?? data["winners"] as JArray;
        var awards = data["awards"] as JContainer ?? lotInfo?["awards"] as JContainer;
        var winners = ParseWinnerList(winnerList, awards, type);
        var currentTime = ReadTimestamp(data["current_time"], data["timestamp"]);
        var resultAwards = ParseAwards(awards);
        return details with
        {
            Timestamp = currentTime > 0 ? currentTime : details.Timestamp,
            RedPocket = details.RedPocket with
            {
                Type = type,
                CurrentTime = currentTime > 0 ? currentTime : details.RedPocket.CurrentTime,
                TotalPrice = ReadNullableDecimal(data["total_price"]) ?? details.RedPocket.TotalPrice,
                TotalBattery = ReadNullableDecimal(data["total_battery"]) ?? details.RedPocket.TotalBattery,
                TotalAwardCount = ReadNullableInt(data["total_num"], data["totalNum"], data["award_num"], data["awardNum"])
                    ?? details.RedPocket.TotalAwardCount,
                HasDetails = lotInfo is not null,
                HasResult = winners.Count > 0,
                Awards = resultAwards.Count > 0 ? resultAwards : details.RedPocket.Awards,
                Winners = winners
            }
        };
    }

    private static IReadOnlyList<RedPocketAward> ParseAwards(JToken? values)
    {
        if (values is null || values.Type == JTokenType.Null)
        {
            return [];
        }

        IEnumerable<(string GiftId, JToken Value)> entries;
        if (values is JObject mapping)
        {
            entries = mapping.Properties().Select(property => (property.Name, property.Value));
        }
        else if (values is JArray array)
        {
            entries = array.Select(value => ("", value));
        }
        else
        {
            throw new InvalidDataException("Red pocket awards are invalid.");
        }

        var awards = new List<RedPocketAward>();
        foreach (var entry in entries)
        {
            if (entry.Value is not JObject award)
            {
                throw new InvalidDataException("Red pocket award is invalid.");
            }

            var giftId = ReadText(award["gift_id"], award["giftId"]);
            awards.Add(new()
            {
                GiftId = giftId.Length > 0 ? giftId : entry.GiftId,
                Name = ReadText(award["gift_name"], award["giftName"], award["award_name"]),
                ImageUrl = ReadText(award["gift_pic"], award["giftPic"], award["award_pic"]),
                Quantity = ReadNullableInt(award["num"], award["gift_num"]),
                Type = ReadNullableInt(award["gift_type"], award["giftType"], award["award_type"]),
                Price = ReadNullableDecimal(award["award_price"], award["price"])
            });
        }

        return awards;
    }

    private static IReadOnlyList<RedPocketWinner> ParseWinnerList(JArray? values, JToken? awards, int? type)
    {
        if (values is null)
        {
            return [];
        }

        var winners = new List<RedPocketWinner>(values.Count);
        foreach (var value in values)
        {
            var winner = value switch
            {
                JArray array => ParseArrayWinner(array, awards, type),
                JObject item => ParseObjectWinner(item, awards, type),
                _ => throw new InvalidDataException("Red pocket winner is invalid.")
            };
            if (winner.Uid <= 0 && string.IsNullOrWhiteSpace(winner.UserName))
            {
                throw new InvalidDataException("Red pocket winner identity is missing or invalid.");
            }

            winners.Add(winner);
        }

        return winners;
    }

    private static RedPocketWinner ParseArrayWinner(JArray values, JToken? awards, int? type)
    {
        if (values.Count < 4)
        {
            throw new InvalidDataException("Red pocket winner fields are incomplete.");
        }

        var giftId = ReadText(values[3]);
        var award = FindAward(awards, giftId);
        var user = ReadArrayItem(values, 8) as JObject;
        var userBase = user?["base"] as JObject;
        var amount = ReadNullableDecimal(ReadArrayItem(values, 5));
        return new()
        {
            Uid = ReadPositiveLong(user?["uid"], values[0]),
            UserName = ReadText(userBase?["name"], values[1]),
            AvatarUrl = ReadText(userBase?["face"]),
            IsMystery = ReadNullableBoolean(userBase?["is_mystery"], ReadArrayItem(values, 4)) ?? false,
            GiftId = giftId,
            GiftName = ReadText(award?["award_name"], award?["gift_name"]),
            GiftImageUrl = ReadText(award?["award_pic"], award?["gift_pic"]),
            Quantity = 1,
            AwardType = ReadNullableInt(award?["award_type"]),
            AwardPrice = ReadNullableDecimal(award?["award_price"]),
            BatteryAmount = type is 3 or 5 ? amount : null,
            IntimacyAmount = type == 4 ? amount : null
        };
    }

    private static RedPocketWinner ParseObjectWinner(JObject value, JToken? awards, int? type)
    {
        var user = value["uinfo"] as JObject;
        var userBase = user?["base"] as JObject;
        var giftId = ReadText(value["gift_id"], value["giftId"], value["award_id"], value["awardId"]);
        var award = FindAward(awards, giftId);
        var batteryAmount = ReadNullableDecimal(value["battery_amount"], value["batteryAmount"]);
        if (batteryAmount is null && type is 3 or 5)
        {
            batteryAmount = ReadNullableDecimal(value["gift_num"], value["giftNum"]);
        }

        return new()
        {
            Uid = ReadPositiveLong(user?["uid"], value["uid"]),
            UserName = ReadText(userBase?["name"], value["uname"], value["name"]),
            AvatarUrl = ReadText(userBase?["face"], value["face"]),
            IsMystery = ReadNullableBoolean(userBase?["is_mystery"], value["is_mystery"]) ?? false,
            GiftId = giftId,
            GiftName = ReadText(value["award_name"], value["awardName"], value["gift_name"], value["giftName"], award?["award_name"], award?["gift_name"]),
            GiftImageUrl = ReadText(value["award_pic"], value["awardPic"], value["gift_pic"], value["giftPic"], award?["award_pic"], award?["gift_pic"]),
            Quantity = Math.Max(1, ReadNullableInt(value["gift_num"], value["giftNum"]) ?? 1),
            AwardType = ReadNullableInt(value["award_type"], value["awardType"], award?["award_type"]),
            AwardPrice = ReadNullableDecimal(value["award_price"], value["awardPrice"], award?["award_price"]),
            BatteryAmount = batteryAmount,
            IntimacyAmount = ReadNullableDecimal(value["intimacy_amount"], value["intimacyAmount"], value["intimacy"], value["intimacy_value"])
        };
    }

    private static JObject? FindAward(JToken? awards, string giftId)
    {
        if (awards is JObject values)
        {
            return values[giftId] as JObject;
        }

        return (awards as JArray)?.OfType<JObject>().FirstOrDefault(value =>
            ReadText(value["gift_id"], value["giftId"], value["id"]) == giftId);
    }

    private static JToken? ReadArrayItem(JArray values, int index)
    {
        return index < values.Count ? values[index] : null;
    }

    private static string ReadText(params JToken?[] tokens)
    {
        foreach (var token in tokens)
        {
            if (token is JValue value)
            {
                var text = Convert.ToString(value.Value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return "";
    }

    private static long ReadPositiveLong(params JToken?[] tokens)
    {
        foreach (var token in tokens)
        {
            if (long.TryParse(ReadText(token), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return 0;
    }

    private static int? ReadNullableInt(params JToken?[] tokens)
    {
        foreach (var token in tokens)
        {
            if (int.TryParse(ReadText(token), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            {
                return value;
            }
        }

        return null;
    }

    private static decimal? ReadNullableDecimal(params JToken?[] tokens)
    {
        foreach (var token in tokens)
        {
            if (decimal.TryParse(ReadText(token), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) && value >= 0)
            {
                return value;
            }
        }

        return null;
    }

    private static bool? ReadNullableBoolean(params JToken?[] tokens)
    {
        foreach (var token in tokens)
        {
            if (bool.TryParse(ReadText(token), out var value))
            {
                return value;
            }

            var integer = ReadNullableInt(token);
            if (integer is 0 or 1)
            {
                return integer == 1;
            }
        }

        return null;
    }

    private static long ReadTimestamp(params JToken?[] tokens)
    {
        foreach (var token in tokens)
        {
            var value = ReadPositiveLong(token);
            if (value > MAX_UNIX_TIMESTAMP_SECONDS)
            {
                value /= 1000;
            }

            if (value is > 0 and <= MAX_UNIX_TIMESTAMP_SECONDS)
            {
                return value;
            }
        }

        return 0;
    }
}
