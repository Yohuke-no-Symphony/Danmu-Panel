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
        AND is_record_visible(sequence,payload_revision,
            CASE WHEN is_cached_message(sequence,payload_revision) THEN NULL ELSE id END,
            CASE WHEN is_cached_message(sequence,payload_revision) THEN NULL ELSE payload END)
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
        DanmuRegexFilter? danmuRegexFilter = null, MessageReadFilter readFilter = MessageReadFilter.All,
        MessageSortOrder sortOrder = MessageSortOrder.OldestFirst)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        if (!Enum.IsDefined(readFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(readFilter));
        }
        if (!Enum.IsDefined(sortOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }
        return EnqueueAsync(connection => ReadFeed(connection, roomTabId, kind, historyLimit, oldestHistoryId,
            hideEmoticonDanmu && kind == MessageKind.Danmu,
            kind == MessageKind.Danmu ? danmuRegexFilter : null,
            kind == MessageKind.Danmu ? MessageReadFilter.All : readFilter, sortOrder, cancellationToken),
            cancellationToken, latestReads);
    }

    private MessageFeedSnapshot ReadFeed(SqliteConnection connection, string roomTabId, MessageKind kind,
        int historyLimit, string? oldestHistoryId, bool hideEmoticons, DanmuRegexFilter? filter,
        MessageReadFilter readFilter, MessageSortOrder sortOrder, CancellationToken cancellationToken,
        bool includeEventSummaries = false)
    {
        return ReadFeedTransaction(connection, latestReadCache, kind, hideEmoticons, filter, includeEventSummaries,
            (transaction, predicate, _) =>
            {
                var (rows, unreadCount) = ReadPriorityRows(connection, transaction, roomTabId, kind,
                    predicate, readFilter, cancellationToken);
                var history = ReadHistoryRows(connection, transaction, roomTabId, kind, historyLimit,
                    oldestHistoryId, predicate + ReadStatusPredicate(readFilter), includeEventSummaries
                        ? DanmuTimelineHistoryPredicate : "kind=$kind AND priority=0",
                    readFilter != MessageReadFilter.All, cancellationToken);
                rows.AddRange(history);
                var records = LoadFeedRecords(connection, transaction, latestReadCache, rows, cancellationToken);
                if (kind != MessageKind.Danmu && readFilter != MessageReadFilter.All)
                {
                    rows.Sort((left, right) => sortOrder == MessageSortOrder.NewestFirst
                        ? CompareChronologically(right, left) : CompareChronologically(left, right));
                }
                var pending = ReadFeedPending(connection, transaction, records.Values, cancellationToken);
                return new MessageFeedSnapshot(rows.Select(row => records[row.Sequence].Record).ToArray(), unreadCount)
                {
                    OldestHistoryId = history.Count > 0 ? records[history[0].Sequence].Id : null,
                    PendingTranslationIds = pending
                };
            }, cancellationToken);
    }

    private T ReadFeedTransaction<T>(SqliteConnection connection, MessageRecordReadCache cache, MessageKind kind,
        bool hideEmoticons, DanmuRegexFilter? filter, bool includeEventSummaries,
        Func<SqliteTransaction, string, decimal, T> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var readCancellation = new SqliteReadCancellation(connection, cancellationToken);
        var filtered = false;
        try
        {
            var giftMinimum = kind == MessageKind.Gift || includeEventSummaries
                ? ReadOptions(connection, transaction).GiftAutoPinMinimum : 0;
            filtered = giftMinimum > 0 || hideEmoticons || filter is { Enabled: true };
            if (filtered)
            {
                RegisterFeedVisibility(connection, cache, hideEmoticons, filter, giftMinimum, cancellationToken);
            }
            var result = read(transaction, filtered ? VISIBILITY_PREDICATE : "", giftMinimum);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return result;
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            if (filtered)
            {
                connection.CreateFunction<long, long, bool>("is_cached_message", null);
                connection.CreateFunction<long, long, string?, string?, bool>("is_record_visible", null);
            }
        }
    }

    private static int CompareChronologically(SqliteFeedRow left, SqliteFeedRow right)
    {
        var time = left.OccurredMilliseconds.CompareTo(right.OccurredMilliseconds);
        return time != 0 ? time : left.Sequence.CompareTo(right.Sequence);
    }

    private static string ReadStatusPredicate(MessageReadFilter readFilter)
    {
        return readFilter switch
        {
            MessageReadFilter.Unread => " AND json_extract(payload,'$.IsRead')=0",
            MessageReadFilter.Read => " AND json_extract(payload,'$.IsRead')=1",
            _ => ""
        };
    }

    private static (List<SqliteFeedRow> Rows, int UnreadCount) ReadPriorityRows(SqliteConnection connection,
        SqliteTransaction transaction, string roomTabId, MessageKind kind, string predicate,
        MessageReadFilter readFilter, CancellationToken cancellationToken)
    {
        var parameters = new (string, object?)[] { ("$room", roomTabId), ("$kind", (int)kind) };
        var readStatus = kind == MessageKind.Danmu ? "1" : "json_extract(payload,'$.IsRead')";
        using var command = Command(connection, transaction, $"""
            SELECT sequence,occurred_ms,payload_revision,{readStatus} FROM message_records INDEXED BY ix_message_priority_time
            WHERE room_tab_id=$room AND kind=$kind AND priority=1 {predicate}
            ORDER BY occurred_ms ASC,sequence ASC;
            """, parameters);
        var rows = new List<SqliteFeedRow>();
        var unreadCount = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readState = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                if (readFilter == MessageReadFilter.All ||
                    readFilter == MessageReadFilter.Unread && readState == 0 ||
                    readFilter == MessageReadFilter.Read && readState == 1)
                {
                    rows.Add(ReadFeedRow(reader));
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
                """, parameters);
            unreadCount += Convert.ToInt32(unread.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return (rows, unreadCount);
    }

    private static List<SqliteFeedRow> ReadHistoryRows(SqliteConnection connection, SqliteTransaction transaction,
        string roomTabId, MessageKind kind, int historyLimit, string? oldestHistoryId, string predicate,
        string selectionPredicate, bool readStateIndex, CancellationToken cancellationToken)
    {
        var index = readStateIndex ? "INDEXED BY ix_message_unread" : "";
        using var command = Command(connection, transaction, $"""
            SELECT sequence,occurred_ms,payload_revision FROM message_records {index}
            WHERE room_tab_id=$room AND {selectionPredicate} {predicate}
            ORDER BY occurred_ms DESC,sequence DESC LIMIT $limit;
            """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", historyLimit));
        var rows = ReadFeedRows(command, cancellationToken);
        if (rows.Count == historyLimit && oldestHistoryId is not null)
        {
            var last = rows[^1];
            using var retained = Command(connection, transaction, $"""
                SELECT sequence,occurred_ms,payload_revision FROM message_records {index}
                WHERE room_tab_id=$room AND {selectionPredicate}
                    AND (occurred_ms,sequence)<($lastTime,$lastSequence)
                    AND (occurred_ms,sequence)>=(
                        SELECT occurred_ms,sequence FROM message_records WHERE id=$oldest)
                    {predicate}
                ORDER BY occurred_ms DESC,sequence DESC;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$lastTime", last.OccurredMilliseconds),
                ("$lastSequence", last.Sequence), ("$oldest", oldestHistoryId));
            rows.AddRange(ReadFeedRows(retained, cancellationToken));
        }
        rows.Reverse();
        return rows;
    }

    private static List<SqliteFeedRow> ReadFeedRows(SqliteCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<SqliteFeedRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(ReadFeedRow(reader));
        }
        return rows;
    }

    private static SqliteFeedRow ReadFeedRow(SqliteDataReader reader)
    {
        return new SqliteFeedRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private void RegisterFeedVisibility(SqliteConnection connection, MessageRecordReadCache cache, bool hideEmoticons,
        DanmuRegexFilter? filter, decimal giftMinimum, CancellationToken cancellationToken)
    {
        var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
        connection.CreateFunction<long, long, bool>("is_cached_message", cache.IsValidated);
        connection.CreateFunction<long, long, string?, string?, bool>("is_record_visible", (sequence, payloadRevision, id, payload) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref feedVisibilityEvaluations);
            var record = payload is not null ? CaptureFeedPayload(cache, sequence, payloadRevision, id!, payload) :
                cache.TryGetValidated(sequence, payloadRevision, out var entry) ? entry.Record :
                throw new InvalidOperationException("A validated message cache entry disappeared during SQL evaluation.");
            if (record.Kind == MessageKind.Gift && record.TotalPrice < giftMinimum)
            {
                return false;
            }
            if (record.Kind != MessageKind.Danmu)
            {
                return true;
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
        SqliteTransaction transaction, MessageRecordReadCache cache, IReadOnlyList<SqliteFeedRow> rows,
        CancellationToken cancellationToken)
    {
        var records = new Dictionary<long, (string Id, MessageRecord Record)>(rows.Count);
        var missing = new List<long>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.TryGetValidated(row.Sequence, row.PayloadRevision, out var entry))
            {
                records.Add(row.Sequence, (entry.Id, entry.Record));
            }
            else
            {
                missing.Add(row.Sequence);
            }
        }
        if (missing.Count == 0)
        {
            return records;
        }
        using var command = Command(connection, transaction, """
            SELECT sequence,id,payload,payload_revision FROM message_records
            WHERE sequence IN (SELECT value FROM json_each($sequences));
            """, ("$sequences", JsonSerializer.Serialize(missing)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = reader.GetInt64(0);
            var id = reader.GetString(1);
            records.Add(sequence, (id, CaptureFeedPayload(cache, sequence, reader.GetInt64(3), id, reader.GetString(2))));
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
            if (entry.Record.TranslationFingerprint is not null)
            {
                visible.Add(entry.Id, entry.Record);
            }
        }
        var pending = new HashSet<string>(StringComparer.Ordinal);
        if (visible.Count == 0)
        {
            return pending;
        }
        using var command = Command(connection, transaction, """
            SELECT job.id,job.generation,job.fingerprint
            FROM json_each($ids) AS page CROSS JOIN translation_jobs AS job ON job.id=page.value
            WHERE job.status=0;
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

    private MessageRecord CaptureFeedPayload(MessageRecordReadCache cache, long sequence, long payloadRevision,
        string id, string payload)
    {
        Interlocked.Increment(ref payloadRowsRead);
        Interlocked.Add(ref payloadTextBytesRead, (long)payload.Length * sizeof(char));
        return cache.GetOrDeserialize(sequence, payloadRevision, id, payload);
    }
}
