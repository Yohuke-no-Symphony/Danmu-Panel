using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    /// <inheritdoc />
    public Task<MessageAppendResult> AppendAsync(MessageRecord message, MessageReadOptions options,
        string? translationFingerprint = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.RoomTabId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Id);
        return EnqueueAsync(connection => Append(connection, message, options, translationFingerprint), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MessageRecord>> LoadPageAsync(string roomTabId, MessageKind kind, int offset, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return EnqueueAsync<IReadOnlyList<MessageRecord>>(connection =>
        {
            using var command = Command(connection, null, """
                SELECT payload FROM message_records WHERE room_tab_id=$room AND kind=$kind
                ORDER BY priority DESC,pinned_ms DESC,occurred_ms DESC,sequence DESC LIMIT $limit OFFSET $offset;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$offset", offset), ("$limit", limit));
            return ReadMessages(command);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MessageRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection => ReadMessage(connection, null, id), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MessageRecord>> LoadRecentAsync(string roomTabId, MessageKind kind, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return EnqueueAsync<IReadOnlyList<MessageRecord>>(connection =>
        {
            using var command = Command(connection, null, """
                SELECT payload FROM message_records WHERE room_tab_id=$room AND kind=$kind AND is_test=0
                ORDER BY occurred_ms DESC,sequence DESC LIMIT $limit;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", limit));
            return ReadMessages(command);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<long> GetGenerationAsync(string roomTabId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            Execute(connection, null, "INSERT OR IGNORE INTO message_rooms(id) VALUES($room);", ("$room", roomTabId));
            return ReadGeneration(connection, null, roomTabId);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MessageRecord?> TogglePinAsync(string id, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var record = ReadMessage(connection, transaction, id);
            if (record is null)
            {
                return null;
            }
            if (record.Kind != MessageKind.Danmu)
            {
                throw new InvalidOperationException("Only ordinary danmu can be manually pinned.");
            }

            record = record with
            {
                IsPinned = !record.IsPinned,
                IsPriority = !record.IsPinned,
                PinnedAt = record.IsPinned ? null : DateTimeOffset.UtcNow
            };
            WriteMessage(connection, transaction, record);
            UpdateJobPriority(connection, transaction, record);
            transaction.Commit();
            return record;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<MessageRecord?> MarkReadAsync(string id, int observedQuantity, decimal observedTotalPrice,
        CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var record = ReadMessage(connection, transaction, id);
            if (record is null)
            {
                return null;
            }
            var options = ReadOptions(connection, transaction);
            record = record with
            {
                HasBeenRead = true,
                ReadQuantity = Math.Max(record.ReadQuantity, Math.Min(record.Quantity, observedQuantity)),
                ReadTotalPrice = Math.Max(record.ReadTotalPrice, Math.Min(record.TotalPrice, observedTotalPrice))
            };
            record = record with { IsRead = record.Kind != MessageKind.Gift || IsGiftRead(record, options) };
            record = record with { IsPriority = IsPriority(record, options) };
            WriteMessage(connection, transaction, record);
            UpdateJobPriority(connection, transaction, record);
            transaction.Commit();
            return record;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> MarkAllReadAsync(string roomTabId, MessageKind kind, CancellationToken cancellationToken = default)
    {
        if (kind is not (MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction, """
                SELECT payload FROM message_records
                WHERE room_tab_id=$room AND kind=$kind AND json_extract(payload,'$.IsRead')=0;
                """, ("$room", roomTabId), ("$kind", (int)kind));
            var records = ReadMessages(command);
            foreach (var record in records)
            {
                var updated = record with
                {
                    HasBeenRead = true,
                    ReadQuantity = record.Quantity,
                    ReadTotalPrice = record.TotalPrice,
                    IsRead = true,
                    IsPriority = false
                };
                WriteMessage(connection, transaction, updated);
                UpdateJobPriority(connection, transaction, updated);
            }
            transaction.Commit();
            return records.Count;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearFeedAsync(string roomTabId, MessageKind kind, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, """
                INSERT OR IGNORE INTO message_rooms(id) VALUES($room);
                INSERT INTO message_feed_clears(room_tab_id,kind,cleared_before) VALUES($room,$kind,$cutoff)
                ON CONFLICT(room_tab_id,kind) DO UPDATE SET cleared_before=excluded.cleared_before;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$cutoff", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            if (kind == MessageKind.Gift)
            {
                Execute(connection, transaction, """
                    UPDATE message_combos SET cleared_quantity=quantity,cleared_total_price=total_price,message_id=NULL
                    WHERE room_tab_id=$room;
                    """, ("$room", roomTabId));
            }
            Execute(connection, transaction, "DELETE FROM message_records WHERE room_tab_id=$room AND kind=$kind;",
                ("$room", roomTabId), ("$kind", (int)kind));
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearRoomAsync(string roomTabId, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            ClearRoom(connection, transaction, roomTabId);
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var rooms = new List<string>();
            using (var command = Command(connection, transaction, "SELECT id FROM message_rooms;"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    rooms.Add(reader.GetString(0));
                }
            }
            foreach (var room in rooms)
            {
                ClearRoom(connection, transaction, room);
            }
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task ReevaluateReadOptionsAsync(MessageReadOptions options, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            WriteOptions(connection, transaction, options);
            using var command = Command(connection, transaction, "SELECT payload FROM message_records WHERE kind=$kind;", ("$kind", (int)MessageKind.Gift));
            foreach (var record in ReadMessages(command))
            {
                var updated = record with { IsPriority = IsPriority(record, options) };
                WriteMessage(connection, transaction, updated);
                UpdateJobPriority(connection, transaction, updated);
            }
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    private static MessageAppendResult Append(SqliteConnection connection, MessageRecord message,
        MessageReadOptions options, string? translationFingerprint)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT OR IGNORE INTO message_rooms(id) VALUES($room);", ("$room", message.RoomTabId));
        if (ReadGeneration(connection, transaction, message.RoomTabId) != message.Generation)
        {
            return new(MessageAppendDisposition.Stale);
        }
        if (message.IsHistory)
        {
            using var cutoff = Command(connection, transaction, """
                SELECT MAX(cleared_before,COALESCE(
                    (SELECT cleared_before FROM message_feed_clears WHERE room_tab_id=$room AND kind=$kind),0))
                FROM message_rooms WHERE id=$room;
                """, ("$room", message.RoomTabId), ("$kind", (int)message.Kind));
            var clearedBefore = Convert.ToInt64(cutoff.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (clearedBefore > 0 && (!message.HasSourceTimestamp || clearedBefore >= message.Time.ToUnixTimeMilliseconds()))
            {
                return new(MessageAppendDisposition.ClearedHistory);
            }
        }
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            var accepted = Execute(connection, transaction, "INSERT OR IGNORE INTO message_events(room_tab_id,kind,source_id) VALUES($room,$kind,$source);",
                ("$room", message.RoomTabId), ("$kind", (int)message.Kind), ("$source", message.MessageId));
            if (accepted == 0)
            {
                return new(MessageAppendDisposition.Duplicate);
            }
        }

        WriteOptions(connection, transaction, options);
        var existing = ReadMessage(connection, transaction, message.Id);
        if (existing is not null)
        {
            return new(MessageAppendDisposition.Duplicate);
        }
        var record = message with
        {
            IsRead = false, IsPinned = false, IsPriority = false, PinnedAt = null,
            HasBeenRead = false, ReadQuantity = 0, ReadTotalPrice = 0,
            Translation = null, TranslationFingerprint = translationFingerprint
        };
        var disposition = MessageAppendDisposition.Appended;
        if (message.Kind == MessageKind.Gift && !string.IsNullOrWhiteSpace(message.ComboId))
        {
            var result = MergeGift(connection, transaction, record, options);
            if (result.Record is null)
            {
                transaction.Commit();
                return result;
            }
            record = result.Record;
            disposition = result.Disposition;
        }
        else if (record.Kind == MessageKind.Gift)
        {
            var quantity = Math.Max(record.Quantity, record.BatchComboCount);
            record = record with { Quantity = quantity, TotalPrice = record.UnitPrice * quantity };
        }

        record = record with { IsPriority = IsPriority(record, options) };
        WriteMessage(connection, transaction, record);
        if (translationFingerprint is not null && record.Kind is MessageKind.Danmu or MessageKind.SuperChat)
        {
            EnsureJob(connection, transaction, record, translationFingerprint);
        }
        transaction.Commit();
        return new(disposition, record);
    }

    private static MessageAppendResult MergeGift(SqliteConnection connection, SqliteTransaction transaction,
        MessageRecord incoming, MessageReadOptions options)
    {
        var key = JsonSerializer.Serialize(new[] { incoming.RoomId, incoming.UserUid.ToString(CultureInfo.InvariantCulture),
            string.IsNullOrEmpty(incoming.GiftId) ? incoming.GiftName : incoming.GiftId, incoming.ComboId });
        GiftComboState? combo = null;
        using (var command = Command(connection, transaction,
                   "SELECT quantity,total_price,cleared_quantity,cleared_total_price,message_id FROM message_combos WHERE room_tab_id=$room AND combo_key=$key;",
                   ("$room", incoming.RoomTabId), ("$key", key)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                combo = new(reader.GetInt32(0), decimal.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    reader.GetInt32(2), decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        var previousCount = combo?.Quantity ?? 0;
        var quantity = incoming.BatchComboCount > 0
            ? Math.Max(previousCount, combo is null ? Math.Max(incoming.Quantity, incoming.BatchComboCount) : incoming.BatchComboCount)
            : checked(previousCount + incoming.Quantity);
        var delta = quantity - previousCount;
        if (delta == 0)
        {
            return new(MessageAppendDisposition.Duplicate);
        }
        var existing = combo?.MessageId is { } id ? ReadMessage(connection, transaction, id) : null;
        var unitPrice = incoming.UnitPrice > 0 ? incoming.UnitPrice :
            existing?.UnitPrice ?? (previousCount > 0 ? combo!.TotalPrice / previousCount : 0);
        var total = (combo?.TotalPrice ?? 0) + unitPrice * delta;
        var record = (existing ?? incoming) with
        {
            Quantity = quantity - (combo?.ClearedQuantity ?? 0),
            BatchComboCount = quantity,
            TotalPrice = total - (combo?.ClearedTotalPrice ?? 0),
            UnitPrice = unitPrice
        };
        record = record with { IsRead = IsGiftRead(record, options) };
        Execute(connection, transaction, """
            INSERT INTO message_combos(room_tab_id,combo_key,quantity,total_price,cleared_quantity,cleared_total_price,message_id)
            VALUES($room,$key,$quantity,$total,$cleared,$clearedTotal,$message)
            ON CONFLICT(room_tab_id,combo_key) DO UPDATE SET quantity=excluded.quantity,total_price=excluded.total_price,message_id=excluded.message_id;
            """, ("$room", incoming.RoomTabId), ("$key", key), ("$quantity", quantity),
            ("$total", total.ToString(CultureInfo.InvariantCulture)), ("$cleared", combo?.ClearedQuantity ?? 0),
            ("$clearedTotal", (combo?.ClearedTotalPrice ?? 0).ToString(CultureInfo.InvariantCulture)), ("$message", record.Id));
        return new(existing is null ? MessageAppendDisposition.Appended : MessageAppendDisposition.Updated, record);
    }

    private static bool IsGiftRead(MessageRecord record, MessageReadOptions options)
    {
        if (!record.HasBeenRead)
        {
            return false;
        }
        return options.GiftReadResetPolicy switch
        {
            GiftReadResetPolicy.ConfirmOnce => true,
            GiftReadResetPolicy.NewQuantity => record.Quantity <= record.ReadQuantity,
            GiftReadResetPolicy.NewValueThreshold => record.TotalPrice <= record.ReadTotalPrice ||
                record.TotalPrice - record.ReadTotalPrice < options.GiftAutoPinMinimum,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
    }

    private static void ClearRoom(SqliteConnection connection, SqliteTransaction transaction, string roomTabId)
    {
        Execute(connection, transaction, """
            INSERT INTO message_rooms(id,generation,cleared_before) VALUES($room,1,$cutoff)
            ON CONFLICT(id) DO UPDATE SET generation=generation+1,cleared_before=excluded.cleared_before;
            UPDATE message_combos SET cleared_quantity=quantity,cleared_total_price=total_price,message_id=NULL WHERE room_tab_id=$room;
            DELETE FROM message_records WHERE room_tab_id=$room;
            """, ("$room", roomTabId), ("$cutoff", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private static List<MessageRecord> ReadMessages(SqliteCommand command)
    {
        var records = new List<MessageRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(JsonSerializer.Deserialize<MessageRecord>(reader.GetString(0))!);
        }
        return records;
    }
}
