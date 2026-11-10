using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    private static string DanmuTimelineHistoryPredicate { get; } = $"""
        (kind=$kind AND priority=0 OR
            kind IN ({(int)MessageKind.SuperChat},{(int)MessageKind.Gift},{(int)MessageKind.Guard})
            AND sequence>COALESCE((
                SELECT cleared_sequence FROM message_timeline_clears WHERE room_tab_id=$room),0)
            AND NOT EXISTS (
                SELECT 1 FROM message_feed_clears
                WHERE room_tab_id=$room AND kind=$kind
                    AND json_extract(message_records.payload,'$.IsHistory')=1
                    AND (json_extract(message_records.payload,'$.HasSourceTimestamp')=0
                        OR cleared_before>=message_records.occurred_ms)))
        """;

    /// <summary>读取普通弹幕及付费事件摘要的时间线，仅普通弹幕置顶，礼物按累计金额门限筛选。</summary>
    public Task<MessageFeedSnapshot> LoadDanmuTimelineAsync(string roomTabId, int historyLimit,
        string? oldestHistoryId, bool hideEmoticonDanmu = false, DanmuRegexFilter? danmuRegexFilter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        return EnqueueAsync(connection => ReadFeed(connection, roomTabId, MessageKind.Danmu, historyLimit,
            oldestHistoryId, hideEmoticonDanmu, danmuRegexFilter, MessageReadFilter.All,
            MessageSortOrder.OldestFirst, cancellationToken, includeEventSummaries: true), cancellationToken);
    }
}
