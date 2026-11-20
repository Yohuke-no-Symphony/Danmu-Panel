using System.Globalization;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private static readonly MessageFeedPage EMPTY_PAGE = new([], false, false);
    private static readonly SqliteFeedPage EMPTY_ROW_PAGE = new([], false, false);

    internal Task<MessageFeedWindow> LoadWindowAsync(string roomTabId, MessageKind kind,
        MessageFeedPageRequest scrolling, MessageFeedPageRequest? pinned = null,
        MessageReadFilter readFilter = MessageReadFilter.All, bool hideEmoticonDanmu = false,
        DanmuRegexFilter? danmuRegexFilter = null, CancellationToken cancellationToken = default)
    {
        return EnqueueWindowAsync(roomTabId, kind, scrolling, pinned, readFilter, hideEmoticonDanmu, danmuRegexFilter,
            null, static result => result.Window, cancellationToken);
    }

    internal Task<MessageFeedWindowResult> LoadNavigatedWindowAsync(string roomTabId, MessageKind kind,
        MessageFeedWindowNavigation navigation, MessageReadFilter readFilter = MessageReadFilter.All,
        bool hideEmoticonDanmu = false, DanmuRegexFilter? danmuRegexFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(navigation.ScrollingCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(navigation.ScrollingFallbackLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(navigation.PageSize);
        if (navigation.ScrollingDirection is not (null or MessageFeedPageDirection.Before or MessageFeedPageDirection.After))
        {
            throw new ArgumentOutOfRangeException(nameof(navigation));
        }
        if (navigation.RetainedScrolling is { } retained)
        {
            ValidatePageRequest(retained);
        }
        if (navigation.EmptyPinnedFallback is { } fallback)
        {
            ValidatePageRequest(fallback);
        }

        return EnqueueWindowAsync(roomTabId, kind, navigation.Scrolling, navigation.Pinned, readFilter,
            hideEmoticonDanmu, danmuRegexFilter, navigation, static result => result, cancellationToken);
    }

    private Task<T> EnqueueWindowAsync<T>(string roomTabId, MessageKind kind, MessageFeedPageRequest scrolling,
        MessageFeedPageRequest? pinned, MessageReadFilter readFilter, bool hideEmoticonDanmu,
        DanmuRegexFilter? danmuRegexFilter, MessageFeedWindowNavigation? navigation,
        Func<MessageFeedWindowResult, T> select, CancellationToken cancellationToken)
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
        var history = IsHistoryRequest(scrolling) || pinned is not null && IsHistoryRequest(pinned) ||
            navigation?.LiveScrollingAnchors is not null ||
            timeline && scrolling.Direction == MessageFeedPageDirection.Latest && navigation?.ReadingAnchor is not null ||
            navigation is { ScrollingDirection: not null, RetainedScrolling: not null, PreviousScrolling.Count: > 0 };
        var cache = history ? historyReadCache : latestReadCache;
        return EnqueueAsync(connection => ReadFeedTransaction(connection, cache, kind,
            timeline && hideEmoticonDanmu, timeline ? danmuRegexFilter : null, timeline,
            (transaction, visibility, giftMinimum) => select(ReadWindow(connection, transaction, cache, roomTabId, kind,
                scrolling, pinned, readFilter, visibility, giftMinimum, navigation, cancellationToken)), cancellationToken),
            cancellationToken, history ? historyReads : latestReads);
    }

    private static (MessageFeedCursor First, MessageFeedCursor Last)? ReadTimelineBounds(SqliteConnection connection,
        SqliteTransaction transaction, string roomTabId, string firstId, string lastId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Command(connection, transaction, """
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
    }

    private static (MessageFeedCursor First, MessageFeedCursor Last, MessageFeedCursor Anchor)? ReadReadingWindowBounds(
        SqliteConnection connection, SqliteTransaction transaction, string roomTabId, MessageKind kind, string selection,
        (string AnchorId, string FirstId, string LastId, int WindowSize) reading, MessageFeedCursor latestFirst,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Command(connection, transaction, $"""
            SELECT occurred_ms,sequence,
                CASE WHEN id=$anchor AND (occurred_ms,sequence)<($latestTime,$latestSequence)
                    THEN CASE WHEN ({selection}) THEN 1 ELSE 0 END ELSE 0 END
            FROM message_records
            WHERE room_tab_id=$room AND id IN ($anchor,$first,$last)
            ORDER BY occurred_ms,sequence;
            """, ("$room", roomTabId), ("$kind", (int)kind), ("$anchor", reading.AnchorId), ("$first", reading.FirstId),
            ("$last", reading.LastId), ("$latestTime", latestFirst.OccurredMilliseconds), ("$latestSequence", latestFirst.Sequence));
        using var reader = command.ExecuteReader();
        MessageFeedCursor? first = null;
        MessageFeedCursor? anchor = null;
        var last = default(MessageFeedCursor);
        while (reader.Read())
        {
            last = new(reader.GetInt64(0), reader.GetInt64(1));
            first ??= last;
            if (reader.GetBoolean(2))
            {
                anchor = last;
            }
        }
        return anchor is { } visible ? (first!.Value, last, visible) : null;
    }

    private MessageFeedWindowResult ReadWindow(SqliteConnection connection, SqliteTransaction transaction,
        MessageRecordReadCache cache, string roomTabId, MessageKind kind, MessageFeedPageRequest scrolling,
        MessageFeedPageRequest? pinned, MessageReadFilter readFilter, string visibility, decimal giftMinimum,
        MessageFeedWindowNavigation? navigation, CancellationToken cancellationToken)
    {
        var timeline = kind == MessageKind.Danmu;
        var unreadGiftPriority = kind == MessageKind.Gift && giftMinimum > 0;
        var prioritySelection = unreadGiftPriority && readFilter == MessageReadFilter.Unread;
        var selection = timeline ? $"{DanmuTimelineHistoryPredicate} {visibility}" :
            prioritySelection ? "kind=$kind AND priority=1" :
            $"kind=$kind{ReadStatusPredicate(readFilter)} {visibility}";
        var readStateIndex = !timeline && !prioritySelection && readFilter != MessageReadFilter.All;
        SqliteFeedPage ReadScrolling(MessageFeedPageRequest request)
        {
            return ReadWindowPage(connection, transaction, roomTabId, kind, request, selection, cancellationToken,
                readStateIndex);
        }

        var (scrollingRequest, historyRows) = navigation is null
            ? (scrolling, ReadScrolling(scrolling))
            : ReadScrollingWindow(connection, transaction, roomTabId, navigation, ReadScrolling, cancellationToken);
        var readingWindowPreserved = false;
        if (timeline && scrollingRequest.Direction == MessageFeedPageDirection.Latest && historyRows.Rows.Count > 0 &&
            navigation is { ReadingAnchor: { } reading } &&
            ReadReadingWindowBounds(connection, transaction, roomTabId, kind, selection, reading,
                historyRows.Rows[0].Cursor, cancellationToken) is { } bounds)
        {
            var limit = Math.Max(navigation.ScrollingCapacity, reading.WindowSize);
            var frozenRequest = new MessageFeedPageRequest(limit, MessageFeedPageDirection.Range, bounds.First, bounds.Last);
            var frozenRows = ReadScrolling(frozenRequest);
            if (frozenRows.Rows.Count == limit && frozenRows.Rows.All(row => row.Sequence != bounds.Anchor.Sequence))
            {
                frozenRequest = frozenRequest with { Boundary = bounds.Anchor };
                frozenRows = ReadScrolling(frozenRequest);
            }
            if (frozenRows.Rows.Any(row => row.Sequence == bounds.Anchor.Sequence))
            {
                scrollingRequest = frozenRequest;
                historyRows = frozenRows;
                readingWindowPreserved = true;
            }
        }
        var priorityRows = EMPTY_ROW_PAGE;
        if (timeline && pinned is not null)
        {
            var pinnedSelection = $"kind=$kind AND priority=1 {visibility}";
            SqliteFeedPage ReadPinned(MessageFeedPageRequest request)
            {
                return ReadWindowPage(connection, transaction, roomTabId, kind, request, pinnedSelection,
                    cancellationToken);
            }

            priorityRows = ReadPinned(pinned);
            if (navigation is not null)
            {
                if (priorityRows.Rows.Count == 0 && navigation.EmptyPinnedFallback is { } retainedPins)
                {
                    pinned = retainedPins;
                    priorityRows = ReadPinned(pinned);
                }
                (pinned, priorityRows) = ReadAdjacentIfEmpty(pinned, priorityRows, navigation.PageSize, ReadPinned);
            }
        }
        var rows = historyRows.Rows.Concat(priorityRows.Rows).ToArray();
        var records = LoadFeedRecords(connection, transaction, cache, rows, cancellationToken);
        var pending = ReadFeedPending(connection, transaction, records.Values, cancellationToken);
        var unreadCount = timeline ? 0 : CountUnread(connection, transaction, roomTabId, kind, unreadGiftPriority);
        return new(new(ToPage(historyRows, records), ToPage(priorityRows, records), unreadCount, pending),
            scrollingRequest, pinned, readingWindowPreserved);
    }

    private static (MessageFeedPageRequest Request, SqliteFeedPage Page) ReadScrollingWindow(SqliteConnection connection,
        SqliteTransaction transaction, string roomTabId, MessageFeedWindowNavigation navigation,
        Func<MessageFeedPageRequest, SqliteFeedPage> readPage, CancellationToken cancellationToken)
    {
        var request = navigation.Scrolling;
        var retained = navigation.RetainedScrolling;
        SqliteFeedPage? recoveredPage = null;
        if (navigation.LiveScrollingAnchors is { } anchors &&
            ReadTimelineBounds(connection, transaction, roomTabId, anchors.FirstId, anchors.LastId, cancellationToken) is { } bounds)
        {
            retained = new(navigation.ScrollingCapacity, MessageFeedPageDirection.Range, bounds.First, bounds.Last);
            if (anchors.Preserve)
            {
                request = retained;
            }
            if (navigation.ScrollingDirection is not null)
            {
                recoveredPage = readPage(retained);
            }
        }

        var previousCount = recoveredPage?.Rows.Count ?? navigation.PreviousScrolling?.Count ?? 0;
        if (navigation.ScrollingDirection is { } direction && retained is not null && previousCount > 0)
        {
            var before = direction == MessageFeedPageDirection.Before;
            var boundary = before ? retained.Boundary : retained.Through;
            var adjacent = readPage(new(navigation.PageSize, direction, boundary));
            var adjacentCount = Math.Min(navigation.ScrollingCapacity, adjacent.Rows.Count);
            var previousIncluded = Math.Min(navigation.ScrollingCapacity - adjacentCount, previousCount);
            MessageFeedCursor first;
            MessageFeedCursor last;
            if (before)
            {
                first = adjacentCount > 0 ? adjacent.Rows[0].Cursor : PreviousCursor(0);
                last = previousIncluded > 0 ? PreviousCursor(previousIncluded - 1) : adjacent.Rows[adjacentCount - 1].Cursor;
            }
            else
            {
                first = previousIncluded > 0
                    ? PreviousCursor(previousCount - previousIncluded)
                    : adjacent.Rows[^adjacentCount].Cursor;
                last = adjacentCount > 0 ? adjacent.Rows[^1].Cursor : PreviousCursor(previousCount - 1);
            }
            request = new(navigation.ScrollingCapacity, MessageFeedPageDirection.Range, first, last);
        }

        var page = request == retained && recoveredPage is { } recovered ? recovered : readPage(request);
        return ReadAdjacentIfEmpty(request, page, navigation.ScrollingFallbackLimit, readPage);

        MessageFeedCursor PreviousCursor(int index)
        {
            return recoveredPage is { } restored ? restored.Rows[index].Cursor : navigation.PreviousScrolling![index].Cursor;
        }
    }

    private static (MessageFeedPageRequest Request, SqliteFeedPage Page) ReadAdjacentIfEmpty(MessageFeedPageRequest request,
        SqliteFeedPage page, int limit, Func<MessageFeedPageRequest, SqliteFeedPage> readPage)
    {
        if (page.Rows.Count == 0 && request.Direction == MessageFeedPageDirection.Range && (page.HasOlder || page.HasNewer))
        {
            request = page.HasOlder ? new(limit, MessageFeedPageDirection.Before, request.Boundary)
                : new(limit, MessageFeedPageDirection.After, request.Through);
            page = readPage(request);
        }
        return (request, page);
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

    private static MessageFeedPage ToPage(SqliteFeedPage page,
        IReadOnlyDictionary<long, (string Id, MessageRecord Record)> records)
    {
        return page.Rows.Count == 0 && !page.HasOlder && !page.HasNewer ? EMPTY_PAGE :
            new(page.Rows.Select(row => new MessageFeedEntry(row.Cursor, records[row.Sequence].Record)).ToArray(),
                page.HasOlder, page.HasNewer);
    }

    private static SqliteFeedPage ReadWindowPage(SqliteConnection connection,
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

        SqliteFeedPage AroundTarget()
        {
            var target = Query("AND id=$target", false, 1, targetId: request.TargetId);
            if (target.Count == 0)
            {
                return EMPTY_ROW_PAGE;
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
            return new(older, hasOlder, hasNewer);
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
                return new(rows, hasOlder, false);
            case MessageFeedPageDirection.Oldest:
                rows = Query("", false, limit);
                hasNewer = TrimSentinel(rows, request.Limit);
                return new(rows, false, hasNewer);
            case MessageFeedPageDirection.Before:
                rows = Query(BEFORE, true, limit, request.Boundary);
                hasOlder = TrimSentinel(rows, request.Limit);
                hasNewer = Query(AFTER, false, 1, rows.Count > 0 ? rows[0].Cursor : request.Boundary).Count > 0;
                rows.Reverse();
                return new(rows, hasOlder, hasNewer);
            case MessageFeedPageDirection.After:
                rows = Query(AFTER, false, limit, request.Boundary);
                hasNewer = TrimSentinel(rows, request.Limit);
                hasOlder = Query(BEFORE, true, 1, rows.Count > 0 ? rows[0].Cursor : request.Boundary).Count > 0;
                return new(rows, hasOlder, hasNewer);
            case MessageFeedPageDirection.Range:
                rows = Query(RANGE, false, limit, request.Boundary, request.Through);
                hasNewer = TrimSentinel(rows, request.Limit) ||
                    Query(AFTER, false, 1, rows.Count > 0 ? rows[^1].Cursor : request.Through).Count > 0;
                hasOlder = Query(BEFORE, true, 1, rows.Count > 0 ? rows[0].Cursor : request.Boundary).Count > 0;
                return new(rows, hasOlder, hasNewer);
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
