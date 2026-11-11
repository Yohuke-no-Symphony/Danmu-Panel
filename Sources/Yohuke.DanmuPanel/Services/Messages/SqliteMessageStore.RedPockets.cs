using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private static MessageRecord SnapshotRedPocketMessage(MessageRecord message)
    {
        if (message.RedPocket is not { } redPocket)
        {
            return message;
        }

        return message with { RedPocket = redPocket.CreateSnapshot() };
    }

    private MessageAppendResult AppendRedPocket(SqliteConnection connection, SqliteTransaction transaction,
        MessageRecord incoming, MessageReadOptions options, out bool keepChanges)
    {
        keepChanges = false;
        var incomingData = incoming.RedPocket!;
        if (incomingData.LotId <= 0)
        {
            throw new InvalidDataException("The red pocket ID must be positive.");
        }

        var id = incomingData.GetRecordId(incoming.RoomTabId, incoming.RoomId);
        var existing = ReadMessage(connection, transaction, id);
        var stage = incomingData.HasResult ? "result" : "start";
        var registered = Execute(connection, transaction, """
            INSERT OR IGNORE INTO message_events(room_tab_id,kind,source_id)
            VALUES($room,$kind,$source);
            """, ("$room", incoming.RoomTabId), ("$kind", (int)MessageKind.RedPocket),
            ("$source", $"{id}:{stage}"));
        if (existing is null && registered == 0)
        {
            return new(MessageAppendDisposition.Duplicate);
        }

        var previousData = existing?.RedPocket ?? new RedPocketData { LotId = incomingData.LotId };
        var mergedData = MergeRedPocketData(previousData, incomingData);
        if (existing is not null && RedPocketContentEquals(previousData, mergedData))
        {
            keepChanges = registered > 0;
            return new(MessageAppendDisposition.Duplicate);
        }

        var firstResult = mergedData.HasResult && !previousData.HasResult;
        var record = (existing ?? incoming) with
        {
            Id = id,
            UserUid = mergedData.SenderUid,
            UserName = mergedData.SenderName,
            Content = mergedData.Danmu,
            RedPocket = mergedData,
            IsRead = existing?.IsRead == true && !firstResult,
            IsPinned = false,
            PinnedAt = null,
            HasBeenRead = existing?.HasBeenRead == true,
            ReadQuantity = existing?.ReadQuantity ?? 0,
            ReadTotalPrice = existing?.ReadTotalPrice ?? 0,
            Translation = null,
            TranslationFingerprint = null
        };
        record = record with { IsPriority = IsPriority(record, options) };
        WriteMessage(connection, transaction, record);
        keepChanges = true;
        return new(existing is null ? MessageAppendDisposition.Appended : MessageAppendDisposition.Updated, record);
    }

    private static RedPocketData MergeRedPocketData(RedPocketData previous, RedPocketData incoming)
    {
        var preferIncomingDetails = incoming.HasDetails &&
            (!previous.HasDetails || incoming.CurrentTime >= previous.CurrentTime);
        var primary = preferIncomingDetails ? incoming : previous;
        var secondary = preferIncomingDetails ? previous : incoming;
        var latest = incoming.CurrentTime >= previous.CurrentTime ? incoming : previous;
        var older = ReferenceEquals(latest, incoming) ? previous : incoming;

        return new()
        {
            LotId = previous.LotId,
            Type = primary.Type ?? secondary.Type,
            SenderUid = primary.SenderUid > 0 ? primary.SenderUid : secondary.SenderUid,
            SenderName = RedPocketText(primary.SenderName, secondary.SenderName),
            SenderAvatarUrl = RedPocketText(primary.SenderAvatarUrl, secondary.SenderAvatarUrl),
            IsMystery = primary.IsMystery ?? secondary.IsMystery,
            StartTime = primary.StartTime > 0 ? primary.StartTime : secondary.StartTime,
            EndTime = primary.EndTime > 0 ? primary.EndTime : secondary.EndTime,
            CurrentTime = Math.Max(previous.CurrentTime, incoming.CurrentTime),
            Duration = primary.Duration ?? secondary.Duration,
            LotStatus = previous.HasResult && !incoming.HasResult
                ? previous.LotStatus ?? incoming.LotStatus
                : latest.LotStatus ?? older.LotStatus,
            UserStatus = latest.UserStatus ?? older.UserStatus,
            JoinRequirement = primary.JoinRequirement ?? secondary.JoinRequirement,
            Danmu = RedPocketText(primary.Danmu, secondary.Danmu),
            H5Url = RedPocketText(primary.H5Url, secondary.H5Url),
            Price = incoming.Price ?? previous.Price,
            TotalPrice = primary.TotalPrice ?? secondary.TotalPrice,
            TotalBattery = primary.TotalBattery ?? secondary.TotalBattery,
            TotalAwardCount = incoming.TotalAwardCount is { } totalAwardCount
                ? Math.Max(previous.TotalAwardCount ?? 0, totalAwardCount)
                : previous.TotalAwardCount,
            HasDetails = previous.HasDetails || incoming.HasDetails,
            HasResult = previous.HasResult || incoming.HasResult,
            Awards = MergeRedPocketAwards(primary.Awards, secondary.Awards),
            Winners = MergeRedPocketWinners(previous.Winners, incoming.HasResult ? incoming.Winners : [])
        };
    }

    private static IReadOnlyList<RedPocketAward> MergeRedPocketAwards(IReadOnlyList<RedPocketAward> primary,
        IReadOnlyList<RedPocketAward> secondary)
    {
        var awards = new Dictionary<string, RedPocketAward>(StringComparer.Ordinal);
        foreach (var award in secondary)
        {
            awards[RedPocketText(award.GiftId, award.Name)] = award;
        }
        foreach (var award in primary)
        {
            var key = RedPocketText(award.GiftId, award.Name);
            if (awards.TryGetValue(key, out var previous))
            {
                awards[key] = award with
                {
                    Name = RedPocketText(award.Name, previous.Name),
                    ImageUrl = RedPocketText(award.ImageUrl, previous.ImageUrl),
                    Quantity = award.Quantity ?? previous.Quantity,
                    Type = award.Type ?? previous.Type,
                    Price = award.Price ?? previous.Price
                };
            }
            else
            {
                awards[key] = award;
            }
        }

        return Array.AsReadOnly(awards.Values.OrderBy(award => award.GiftId, StringComparer.Ordinal)
            .ThenBy(award => award.Name, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<RedPocketWinner> MergeRedPocketWinners(IReadOnlyList<RedPocketWinner> previous,
        IReadOnlyList<RedPocketWinner> incoming)
    {
        var winners = new Dictionary<(long Uid, string GiftId, string AnonymousName, int Occurrence), RedPocketWinner>();
        MergeRedPocketWinnerSnapshot(winners, previous);
        MergeRedPocketWinnerSnapshot(winners, incoming);
        return Array.AsReadOnly(winners.OrderBy(item => item.Key.Uid)
            .ThenBy(item => item.Key.GiftId, StringComparer.Ordinal)
            .ThenBy(item => item.Key.AnonymousName, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Occurrence).Select(item => item.Value).ToArray());
    }

    private static void MergeRedPocketWinnerSnapshot(
        Dictionary<(long Uid, string GiftId, string AnonymousName, int Occurrence), RedPocketWinner> winners,
        IReadOnlyList<RedPocketWinner> snapshot)
    {
        var occurrences = new Dictionary<(long Uid, string GiftId, string AnonymousName), int>();
        foreach (var winner in snapshot)
        {
            var identity = (Uid: winner.Uid, GiftId: winner.GiftId,
                AnonymousName: winner.Uid > 0 ? "" : winner.UserName);
            occurrences.TryGetValue(identity, out var occurrence);
            occurrences[identity] = occurrence + 1;
            var key = (identity.Uid, identity.GiftId, identity.AnonymousName, occurrence);
            if (winners.TryGetValue(key, out var previous))
            {
                winners[key] = winner with
                {
                    UserName = RedPocketText(winner.UserName, previous.UserName),
                    AvatarUrl = RedPocketText(winner.AvatarUrl, previous.AvatarUrl),
                    IsMystery = winner.IsMystery || previous.IsMystery,
                    GiftName = RedPocketText(winner.GiftName, previous.GiftName),
                    GiftImageUrl = RedPocketText(winner.GiftImageUrl, previous.GiftImageUrl),
                    Quantity = Math.Max(winner.Quantity, previous.Quantity),
                    AwardType = winner.AwardType ?? previous.AwardType,
                    AwardPrice = winner.AwardPrice ?? previous.AwardPrice,
                    BatteryAmount = winner.BatteryAmount ?? previous.BatteryAmount,
                    IntimacyAmount = winner.IntimacyAmount ?? previous.IntimacyAmount
                };
            }
            else
            {
                winners[key] = winner;
            }
        }
    }

    private static bool RedPocketContentEquals(RedPocketData previous, RedPocketData current)
    {
        return (previous with { Awards = current.Awards, Winners = current.Winners }) == current &&
            previous.Awards.SequenceEqual(current.Awards) && previous.Winners.SequenceEqual(current.Winners);
    }

    private static string RedPocketText(string primary, string secondary)
    {
        return string.IsNullOrWhiteSpace(primary) ? secondary : primary;
    }
}
