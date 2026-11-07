using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private const int MAX_FILTER_CONTENT_CACHE_COUNT = 1024;
    private const string VISIBILITY_PREDICATE = """
        AND is_record_visible(sequence,
            CASE WHEN is_cached_message(sequence) THEN NULL ELSE id END,
            CASE WHEN is_cached_message(sequence) THEN NULL ELSE payload END)
        """;
    private long payloadRowsRead;
    private long payloadTextBytesRead;
    private long feedVisibilityEvaluations;

    internal long PayloadRowsRead => Interlocked.Read(ref payloadRowsRead);
    internal long PayloadTextBytesRead => Interlocked.Read(ref payloadTextBytesRead);
    internal long FeedVisibilityEvaluations => Interlocked.Read(ref feedVisibilityEvaluations);

    /// <summary>在同一读取事务中取得按阅读状态筛选的消息窗口及完整未读条数，普通弹幕忽略阅读筛选。</summary>
    public Task<MessageFeedSnapshot> LoadFeedAsync(string roomTabId, MessageKind kind, int historyLimit,
        string? oldestHistoryId, CancellationToken cancellationToken = default, bool hideEmoticonDanmu = false,
        DanmuRegexFilter? danmuRegexFilter = null, MessageReadFilter readFilter = MessageReadFilter.All)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        if (!Enum.IsDefined(readFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(readFilter));
        }
        return EnqueueAsync(connection => ReadFeed(connection, roomTabId, kind, historyLimit, oldestHistoryId,
            hideEmoticonDanmu && kind == MessageKind.Danmu,
            kind == MessageKind.Danmu ? danmuRegexFilter : null,
            kind == MessageKind.Danmu ? MessageReadFilter.All : readFilter, cancellationToken), cancellationToken);
    }

    private MessageFeedSnapshot ReadFeed(SqliteConnection connection, string roomTabId, MessageKind kind,
        int historyLimit, string? oldestHistoryId, bool hideEmoticons, DanmuRegexFilter? filter,
        MessageReadFilter readFilter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var readCancellation = new SqliteReadCancellation(connection, cancellationToken);
        var filtered = false;
        try
        {
            using (var version = Command(connection, transaction, "PRAGMA data_version;"))
            {
                readCache.BeginSnapshot(Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture));
            }
            var giftMinimum = kind == MessageKind.Gift ? ReadOptions(connection, transaction).GiftAutoPinMinimum : 0;
            filtered = giftMinimum > 0 || hideEmoticons || filter is { Enabled: true };
            var predicate = filtered ? VISIBILITY_PREDICATE : "";
            if (filtered)
            {
                RegisterFeedVisibility(connection, hideEmoticons, filter, giftMinimum, cancellationToken);
            }
            var (sequences, unreadCount) = ReadPrioritySequences(connection, transaction, roomTabId, kind,
                predicate, readFilter, cancellationToken);
            var readPredicate = readFilter switch
            {
                MessageReadFilter.Unread => " AND json_extract(payload,'$.IsRead')=0",
                MessageReadFilter.Read => " AND json_extract(payload,'$.IsRead')=1",
                _ => ""
            };
            sequences.AddRange(ReadHistorySequences(connection, transaction, roomTabId, kind, historyLimit,
                oldestHistoryId, predicate + readPredicate, cancellationToken));
            var records = LoadFeedRecords(connection, transaction, sequences, cancellationToken);
            var pending = ReadFeedPending(connection, transaction, records.Values, cancellationToken);
            var resultRecords = new MessageRecord[sequences.Count];
            for (var index = 0; index < sequences.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                resultRecords[index] = records[sequences[index]].Record;
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new MessageFeedSnapshot(resultRecords, unreadCount)
            {
                PendingTranslationIds = pending
            };
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
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

    private static (List<long> Sequences, int UnreadCount) ReadPrioritySequences(SqliteConnection connection,
        SqliteTransaction transaction, string roomTabId, MessageKind kind, string predicate,
        MessageReadFilter readFilter, CancellationToken cancellationToken)
    {
        var readStatus = kind == MessageKind.Danmu ? "1" : "json_extract(payload,'$.IsRead')";
        using var command = Command(connection, transaction, $"""
            SELECT sequence,{readStatus} FROM message_records INDEXED BY ix_message_page
            WHERE room_tab_id=$room AND kind=$kind AND priority=1 {predicate}
            ORDER BY occurred_ms ASC,sequence ASC;
            """, ("$room", roomTabId), ("$kind", (int)kind));
        var sequences = new List<long>();
        var unreadCount = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readState = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                if (readFilter == MessageReadFilter.All ||
                    readFilter == MessageReadFilter.Unread && readState == 0 ||
                    readFilter == MessageReadFilter.Read && readState == 1)
                {
                    sequences.Add(reader.GetInt64(0));
                }
                if (readState == 0)
                {
                    unreadCount++;
                }
            }
        }
        if (kind != MessageKind.Danmu)
        {
            using var unread = Command(connection, transaction, $"""
                SELECT COUNT(*) FROM message_records
                WHERE room_tab_id=$room AND kind=$kind AND priority=0
                    AND json_extract(payload,'$.IsRead')=0 {predicate};
                """, ("$room", roomTabId), ("$kind", (int)kind));
            unreadCount += Convert.ToInt32(unread.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return (sequences, unreadCount);
    }

    private static List<long> ReadHistorySequences(SqliteConnection connection, SqliteTransaction transaction,
        string roomTabId, MessageKind kind, int historyLimit, string? oldestHistoryId, string predicate,
        CancellationToken cancellationToken)
    {
        using var command = Command(connection, transaction, $"""
            SELECT sequence,occurred_ms FROM message_records
            WHERE room_tab_id=$room AND kind=$kind AND priority=0 {predicate}
            ORDER BY occurred_ms DESC,sequence DESC LIMIT $limit;
            """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", historyLimit));
        var sequences = new List<long>();
        var lastTime = 0L;
        var lastSequence = 0L;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastSequence = reader.GetInt64(0);
                lastTime = reader.GetInt64(1);
                sequences.Add(lastSequence);
            }
        }
        if (sequences.Count == historyLimit && oldestHistoryId is not null)
        {
            using var retained = Command(connection, transaction, $"""
                SELECT sequence FROM message_records
                WHERE room_tab_id=$room AND kind=$kind AND priority=0
                    AND (occurred_ms,sequence)<($lastTime,$lastSequence)
                    AND (occurred_ms,sequence)>=(
                        SELECT occurred_ms,sequence FROM message_records WHERE id=$oldest)
                    {predicate}
                ORDER BY occurred_ms DESC,sequence DESC;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$lastTime", lastTime),
                ("$lastSequence", lastSequence), ("$oldest", oldestHistoryId));
            using var reader = retained.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                sequences.Add(reader.GetInt64(0));
            }
        }
        sequences.Reverse();
        return sequences;
    }

    private void RegisterFeedVisibility(SqliteConnection connection, bool hideEmoticons, DanmuRegexFilter? filter,
        decimal giftMinimum, CancellationToken cancellationToken)
    {
        var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
        connection.CreateFunction<long, bool>("is_cached_message", readCache.IsValidated);
        connection.CreateFunction<long, string?, string?, bool>("is_record_visible", (sequence, id, payload) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref feedVisibilityEvaluations);
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
                cancellationToken.ThrowIfCancellationRequested();
                blocked = filter.IsBlocked(record.Content);
                cancellationToken.ThrowIfCancellationRequested();
                if (matches.Count < MAX_FILTER_CONTENT_CACHE_COUNT)
                {
                    matches.Add(record.Content, blocked);
                }
            }
            return !blocked;
        });
    }

    private Dictionary<long, (string Id, MessageRecord Record)> LoadFeedRecords(SqliteConnection connection,
        SqliteTransaction transaction, IReadOnlyList<long> sequences, CancellationToken cancellationToken)
    {
        var records = new Dictionary<long, (string Id, MessageRecord Record)>(sequences.Count);
        var missing = new List<long>();
        foreach (var sequence in sequences)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = reader.GetInt64(0);
            var id = reader.GetString(1);
            records.Add(sequence, (id, CaptureFeedPayload(sequence, id, reader.GetString(2))));
        }
        return records;
    }

    private static HashSet<string> ReadFeedPending(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<(string Id, MessageRecord Record)> records, CancellationToken cancellationToken)
    {
        var visible = new Dictionary<string, MessageRecord>(StringComparer.Ordinal);
        foreach (var entry in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            visible.Add(entry.Id, entry.Record);
        }
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
            cancellationToken.ThrowIfCancellationRequested();
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
