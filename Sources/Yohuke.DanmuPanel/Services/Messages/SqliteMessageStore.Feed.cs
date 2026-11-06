using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private const string VISIBILITY_PREDICATE = """
        AND is_record_visible(sequence,
            CASE WHEN is_cached_message(sequence) THEN NULL ELSE id END,
            CASE WHEN is_cached_message(sequence) THEN NULL ELSE payload END)
        """;
    private long payloadRowsRead;
    private long payloadTextBytesRead;

    internal long PayloadRowsRead => Interlocked.Read(ref payloadRowsRead);
    internal long PayloadTextBytesRead => Interlocked.Read(ref payloadTextBytesRead);

    /// <summary>在同一读取事务中取得完整未读条数和消息窗口，仅补读缺失或变化的正文。</summary>
    public Task<MessageFeedSnapshot> LoadFeedAsync(string roomTabId, MessageKind kind, int historyLimit,
        string? oldestHistoryId, CancellationToken cancellationToken = default, bool hideEmoticonDanmu = false,
        DanmuRegexFilter? danmuRegexFilter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        return EnqueueAsync(connection => ReadFeed(connection, roomTabId, kind, historyLimit, oldestHistoryId,
            hideEmoticonDanmu && kind == MessageKind.Danmu,
            kind == MessageKind.Danmu ? danmuRegexFilter : null), cancellationToken);
    }

    private MessageFeedSnapshot ReadFeed(SqliteConnection connection, string roomTabId, MessageKind kind,
        int historyLimit, string? oldestHistoryId, bool hideEmoticons, DanmuRegexFilter? filter)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var version = Command(connection, transaction, "PRAGMA data_version;"))
        {
            readCache.BeginSnapshot(Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        var giftMinimum = kind == MessageKind.Gift ? ReadOptions(connection, transaction).GiftAutoPinMinimum : 0;
        var filtered = giftMinimum > 0 || hideEmoticons || filter is { Enabled: true };
        var predicate = filtered ? VISIBILITY_PREDICATE : "";
        if (filtered)
        {
            RegisterFeedVisibility(connection, hideEmoticons, filter, giftMinimum);
        }
        try
        {
            var retainedCount = 0;
            if (oldestHistoryId is not null)
            {
                using var count = Command(connection, transaction, $"""
                    SELECT COUNT(*) FROM message_records
                    WHERE room_tab_id=$room AND kind=$kind AND priority=0
                        {predicate}
                        AND (occurred_ms,sequence)>=(
                            SELECT occurred_ms,sequence FROM message_records WHERE id=$oldest);
                    """, ("$room", roomTabId), ("$kind", (int)kind), ("$oldest", oldestHistoryId));
                retainedCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var priorityCount = Command(connection, transaction, $"""
                SELECT COUNT(*) FROM message_records
                WHERE room_tab_id=$room AND kind=$kind AND priority=1 {predicate};
                """, ("$room", roomTabId), ("$kind", (int)kind));
            var pinnedCount = Convert.ToInt32(priorityCount.ExecuteScalar(), CultureInfo.InvariantCulture);
            var unreadCount = 0;
            if (kind != MessageKind.Danmu)
            {
                using var unread = Command(connection, transaction, $"""
                    SELECT COUNT(*) FROM message_records
                    WHERE room_tab_id=$room AND kind=$kind AND json_extract(payload,'$.IsRead')=0 {predicate};
                    """, ("$room", roomTabId), ("$kind", (int)kind));
                unreadCount = Convert.ToInt32(unread.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var totalLimit = pinnedCount + Math.Max(retainedCount, historyLimit);
            using var command = Command(connection, transaction, $"""
                SELECT sequence FROM (
                    SELECT sequence,priority,occurred_ms FROM message_records
                    WHERE room_tab_id=$room AND kind=$kind {predicate}
                    ORDER BY priority DESC,pinned_ms DESC,occurred_ms DESC,sequence DESC LIMIT $limit
                ) AS messages
                ORDER BY priority DESC,occurred_ms ASC,sequence ASC;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", totalLimit));
            var sequences = new List<long>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    sequences.Add(reader.GetInt64(0));
                }
            }
            var records = LoadFeedRecords(connection, transaction, sequences);
            var pending = ReadFeedPending(connection, transaction, records.Values);
            transaction.Commit();
            return new MessageFeedSnapshot(sequences.Select(sequence => records[sequence].Record).ToArray(), unreadCount)
            {
                PendingTranslationIds = pending
            };
        }
        finally
        {
            if (filtered)
            {
                connection.CreateFunction<long, bool>("is_cached_message", null);
                connection.CreateFunction<long, string?, string?, bool>("is_record_visible", null);
            }
        }
    }

    private void RegisterFeedVisibility(SqliteConnection connection, bool hideEmoticons, DanmuRegexFilter? filter,
        decimal giftMinimum)
    {
        var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
        connection.CreateFunction<long, bool>("is_cached_message", readCache.IsValidated);
        connection.CreateFunction<long, string?, string?, bool>("is_record_visible", (sequence, id, payload) =>
        {
            var record = payload is not null ? CaptureFeedPayload(sequence, id!, payload) :
                readCache.TryGetValidated(sequence, out var entry) ? entry.Record :
                throw new InvalidOperationException("A validated message cache entry disappeared during SQL evaluation.");
            if (record.Kind == MessageKind.Gift && record.TotalPrice < giftMinimum)
            {
                return false;
            }
            if (hideEmoticons && TranslationTextExtractor.IsEmoticonOnly(record.Content,
                    record.Emoticons.Select(emoticon => emoticon.Text)))
            {
                return false;
            }
            if (filter is not { Enabled: true })
            {
                return true;
            }
            if (!matches.TryGetValue(record.Content, out var blocked))
            {
                blocked = filter.IsBlocked(record.Content);
                matches.Add(record.Content, blocked);
            }
            return !blocked;
        });
    }

    private Dictionary<long, (string Id, MessageRecord Record)> LoadFeedRecords(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<long> sequences)
    {
        var records = new Dictionary<long, (string Id, MessageRecord Record)>(sequences.Count);
        var missing = new List<long>();
        foreach (var sequence in sequences)
        {
            if (readCache.TryGetValidated(sequence, out var entry))
            {
                records.Add(sequence, (entry.Id, entry.Record));
            }
            else
            {
                missing.Add(sequence);
            }
        }
        if (missing.Count == 0)
        {
            return records;
        }
        using var command = Command(connection, transaction, """
            SELECT sequence,id,payload FROM message_records
            WHERE sequence IN (SELECT value FROM json_each($sequences));
            """, ("$sequences", JsonSerializer.Serialize(missing)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sequence = reader.GetInt64(0);
            var id = reader.GetString(1);
            records.Add(sequence, (id, CaptureFeedPayload(sequence, id, reader.GetString(2))));
        }
        return records;
    }

    private static HashSet<string> ReadFeedPending(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<(string Id, MessageRecord Record)> records)
    {
        var visible = records.ToDictionary(entry => entry.Id, entry => entry.Record, StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        if (visible.Count == 0)
        {
            return pending;
        }
        using var command = Command(connection, transaction, """
            SELECT id,generation,fingerprint FROM translation_jobs
            WHERE id IN (SELECT value FROM json_each($ids)) AND status=0;
            """, ("$ids", JsonSerializer.Serialize(visible.Keys)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var record = visible[id];
            if (record.Generation == reader.GetInt64(1) && record.TranslationFingerprint == reader.GetString(2))
            {
                pending.Add(record.Id);
            }
        }
        return pending;
    }

    private MessageRecord CaptureFeedPayload(long sequence, string id, string payload)
    {
        Interlocked.Increment(ref payloadRowsRead);
        Interlocked.Add(ref payloadTextBytesRead, (long)payload.Length * sizeof(char));
        return readCache.GetOrDeserialize(sequence, id, payload);
    }
}
