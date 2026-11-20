using System.Globalization;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private static readonly MessageFeedPage EMPTY_PAGE = new([], false, false);

    internal Task<MessageFeedWindow> LoadWindowAsync(string roomTabId, MessageKind kind,
        MessageFeedPageRequest scrolling, MessageFeedPageRequest? pinned = null,
        MessageReadFilter readFilter = MessageReadFilter.All, bool hideEmoticonDanmu = false,
        DanmuRegexFilter? danmuRegexFilter = null, CancellationToken cancellationToken = default)
    {
        ValidatePageRequest(scrolling);
        if (pinned is not null)
        {
            ValidatePageRequest(pinned);
        }
        if (!Enum.IsDefined(readFilter))
        {
            throw new ArgumentOutOfRangeException(nameof(readFilter));
        }
        var timeline = kind == MessageKind.Danmu;
        var history = IsHistoryRequest(scrolling) || pinned is not null && IsHistoryRequest(pinned);
        var cache = history ? historyReadCache : latestReadCache;
        return EnqueueAsync(connection => ReadFeedTransaction(connection, cache, kind,
            timeline && hideEmoticonDanmu, timeline ? danmuRegexFilter : null, timeline,
            (transaction, visibility, giftMinimum) => ReadWindow(connection, transaction, cache, roomTabId, kind,
                scrolling, pinned, readFilter, visibility, giftMinimum, cancellationToken), cancellationToken),
            cancellationToken, history ? historyReads : latestReads);
    }

    internal Task<(MessageFeedCursor First, MessageFeedCursor Last)?> GetTimelineBoundsAsync(string roomTabId,
        string firstId, string lastId, CancellationToken cancellationToken)
    {
        return EnqueueAsync<(MessageFeedCursor, MessageFeedCursor)?>(connection =>
        {
            using var command = Command(connection, null, """
                SELECT occurred_ms,sequence FROM message_records
                WHERE room_tab_id=$room AND id IN ($first,$last)
                ORDER BY occurred_ms,sequence;
                """, ("$room", roomTabId), ("$first", firstId), ("$last", lastId));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            var first = new MessageFeedCursor(reader.GetInt64(0), reader.GetInt64(1));
            return (first, reader.Read() ? new MessageFeedCursor(reader.GetInt64(0), reader.GetInt64(1)) : first);
        }, cancellationToken, historyReads);
    }

    private MessageFeedWindow ReadWindow(SqliteConnection connection, SqliteTransaction transaction,
        MessageRecordReadCache cache, string roomTabId, MessageKind kind, MessageFeedPageRequest scrolling,
        MessageFeedPageRequest? pinned, MessageReadFilter readFilter, string visibility, decimal giftMinimum,
        CancellationToken cancellationToken)
    {
        var timeline = kind == MessageKind.Danmu;
        var unreadGiftPriority = kind == MessageKind.Gift && giftMinimum > 0;
        var prioritySelection = unreadGiftPriority && readFilter == MessageReadFilter.Unread;
        var selection = timeline ? $"{DanmuTimelineHistoryPredicate} {visibility}" :
            prioritySelection ? "kind=$kind AND priority=1" :
            $"kind=$kind{ReadStatusPredicate(readFilter)} {visibility}";
        var readStateIndex = !timeline && !prioritySelection && readFilter != MessageReadFilter.All;
        var historyRows = ReadWindowPage(connection, transaction, roomTabId, kind, scrolling, selection, cancellationToken,
            readStateIndex);
        var priorityRows = timeline && pinned is not null
            ? ReadWindowPage(connection, transaction, roomTabId, kind, pinned, $"kind=$kind AND priority=1 {visibility}",
                cancellationToken)
            : (Rows: new List<SqliteFeedRow>(), HasOlder: false, HasNewer: false);

        var rows = historyRows.Rows.Concat(priorityRows.Rows).ToArray();
        var records = LoadFeedRecords(connection, transaction, cache, rows, cancellationToken);
        var pending = ReadFeedPending(connection, transaction, records.Values, cancellationToken);
        var unreadCount = timeline ? 0 : CountUnread(connection, transaction, roomTabId, kind, unreadGiftPriority);
        return new(ToPage(historyRows, records), ToPage(priorityRows, records), unreadCount, pending);
    }

    private static int CountUnread(SqliteConnection connection, SqliteTransaction transaction, string roomTabId,
        MessageKind kind, bool unreadGiftPriority)
    {
        var unread = unreadGiftPriority ? "AND priority=1" : ReadStatusPredicate(MessageReadFilter.Unread);
        using var command = Command(connection, transaction, $"""
            SELECT COUNT(*) FROM message_records WHERE room_tab_id=$room AND kind=$kind {unread};
            """, ("$room", roomTabId), ("$kind", (int)kind));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static MessageFeedPage ToPage((List<SqliteFeedRow> Rows, bool HasOlder, bool HasNewer) page,
        IReadOnlyDictionary<long, (string Id, MessageRecord Record)> records)
    {
        return page.Rows.Count == 0 && !page.HasOlder && !page.HasNewer ? EMPTY_PAGE :
            new(page.Rows.Select(row => new MessageFeedEntry(row.Cursor, records[row.Sequence].Record)).ToArray(),
                page.HasOlder, page.HasNewer);
    }

    private static (List<SqliteFeedRow> Rows, bool HasOlder, bool HasNewer) ReadWindowPage(SqliteConnection connection,
        SqliteTransaction transaction, string roomTabId, MessageKind kind, MessageFeedPageRequest request,
        string selection, CancellationToken cancellationToken, bool readStateIndex = false)
    {
        List<SqliteFeedRow> Query(string boundarySql, bool descending, long limit, MessageFeedCursor? boundary = null,
            MessageFeedCursor? through = null, string? targetId = null)
        {
            if (limit == 0)
            {
                return [];
            }
            cancellationToken.ThrowIfCancellationRequested();
            var sort = descending ? "occurred_ms DESC,sequence DESC" : "occurred_ms ASC,sequence ASC";
            var index = readStateIndex && targetId is null ? "INDEXED BY ix_message_unread" : "";
            using var command = Command(connection, transaction, $"""
                SELECT sequence,occurred_ms,payload_revision FROM message_records {index}
                WHERE room_tab_id=$room AND {selection} {boundarySql}
                ORDER BY {sort} LIMIT $limit;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", limit),
                ("$time", boundary?.OccurredMilliseconds), ("$sequence", boundary?.Sequence),
                ("$throughTime", through?.OccurredMilliseconds), ("$throughSequence", through?.Sequence),
                ("$target", targetId));
            return ReadFeedRows(command, cancellationToken);
        }

        const string BEFORE = "AND (occurred_ms,sequence)<($time,$sequence)";
        const string AFTER = "AND (occurred_ms,sequence)>($time,$sequence)";
        const string RANGE = """
            AND (occurred_ms,sequence)>=($time,$sequence) AND (occurred_ms,sequence)<=($throughTime,$throughSequence)
            """;

        (List<SqliteFeedRow>, bool, bool) AroundTarget()
        {
            var target = Query("AND id=$target", false, 1, targetId: request.TargetId);
            if (target.Count == 0)
            {
                return ([], false, false);
            }
            var olderLimit = (request.Limit - 1) / 2;
            var older = Query(BEFORE, true, (long)olderLimit + 1, target[0].Cursor);
            var hasOlder = TrimSentinel(older, olderLimit);
            var newerLimit = request.Limit - older.Count - 1;
            var newer = Query(AFTER, false, (long)newerLimit + 1, target[0].Cursor);
            var hasNewer = TrimSentinel(newer, newerLimit);
            var remaining = newerLimit - newer.Count;
            if (remaining > 0 && hasOlder)
            {
                var extra = Query(BEFORE, true, (long)remaining + 1, older.Count > 0 ? older[^1].Cursor : target[0].Cursor);
                hasOlder = TrimSentinel(extra, remaining);
                older.AddRange(extra);
            }
            older.Reverse();
            older.Add(target[0]);
            older.AddRange(newer);
            return (older, hasOlder, hasNewer);
        }

        var limit = (long)request.Limit + 1;
        List<SqliteFeedRow> rows;
        bool hasOlder;
        bool hasNewer;
        switch (request.Direction)
        {
            case MessageFeedPageDirection.Latest:
                rows = Query("", true, limit);
                hasOlder = TrimSentinel(rows, request.Limit);
                rows.Reverse();
                return (rows, hasOlder, false);
            case MessageFeedPageDirection.Oldest:
                rows = Query("", false, limit);
                hasNewer = TrimSentinel(rows, request.Limit);
                return (rows, false, hasNewer);
            case MessageFeedPageDirection.Before:
                rows = Query(BEFORE, true, limit, request.Boundary);
                hasOlder = TrimSentinel(rows, request.Limit);
                hasNewer = Query(AFTER, false, 1, rows.Count > 0 ? rows[0].Cursor : request.Boundary).Count > 0;
                rows.Reverse();
                return (rows, hasOlder, hasNewer);
            case MessageFeedPageDirection.After:
                rows = Query(AFTER, false, limit, request.Boundary);
                hasNewer = TrimSentinel(rows, request.Limit);
                hasOlder = Query(BEFORE, true, 1, rows.Count > 0 ? rows[0].Cursor : request.Boundary).Count > 0;
                return (rows, hasOlder, hasNewer);
            case MessageFeedPageDirection.Range:
                rows = Query(RANGE, false, limit, request.Boundary, request.Through);
                hasNewer = TrimSentinel(rows, request.Limit) ||
                    Query(AFTER, false, 1, rows.Count > 0 ? rows[^1].Cursor : request.Through).Count > 0;
                hasOlder = Query(BEFORE, true, 1, rows.Count > 0 ? rows[0].Cursor : request.Boundary).Count > 0;
                return (rows, hasOlder, hasNewer);
            case MessageFeedPageDirection.Around:
                return AroundTarget();
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private static bool TrimSentinel(List<SqliteFeedRow> rows, int limit)
    {
        if (rows.Count <= limit)
        {
            return false;
        }
        rows.RemoveAt(rows.Count - 1);
        return true;
    }

    private static bool IsHistoryRequest(MessageFeedPageRequest request)
    {
        return request.Direction is MessageFeedPageDirection.Before or MessageFeedPageDirection.After
            or MessageFeedPageDirection.Around or MessageFeedPageDirection.Range;
    }

    private static void ValidatePageRequest(MessageFeedPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Limit);
        if (!Enum.IsDefined(request.Direction))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
        switch (request.Direction)
        {
            case MessageFeedPageDirection.Before:
            case MessageFeedPageDirection.After:
                ArgumentNullException.ThrowIfNull(request.Boundary);
                break;
            case MessageFeedPageDirection.Range:
                ArgumentNullException.ThrowIfNull(request.Boundary);
                ArgumentNullException.ThrowIfNull(request.Through);
                if (request.Boundary.Value.OccurredMilliseconds > request.Through.Value.OccurredMilliseconds ||
                    request.Boundary.Value.OccurredMilliseconds == request.Through.Value.OccurredMilliseconds &&
                    request.Boundary.Value.Sequence > request.Through.Value.Sequence)
                {
                    throw new ArgumentException("The start of a window must precede its end.", nameof(request));
                }
                break;
            case MessageFeedPageDirection.Around:
                ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetId);
                break;
        }
    }
}
