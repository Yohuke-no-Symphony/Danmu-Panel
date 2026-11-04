using System.Globalization;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    /// <summary>在同一读取事务中取得完整未读条数和消息窗口，并保留历史边界。</summary>
    public Task<MessageFeedSnapshot> LoadFeedAsync(string roomTabId, MessageKind kind, int historyLimit,
        string? oldestHistoryId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var retainedCount = 0;
            if (oldestHistoryId is not null)
            {
                using var count = Command(connection, transaction, """
                    SELECT COUNT(*) FROM message_records
                    WHERE room_tab_id=$room AND kind=$kind AND priority=0
                        AND (occurred_ms,sequence)>=(
                            SELECT occurred_ms,sequence FROM message_records WHERE id=$oldest);
                    """, ("$room", roomTabId), ("$kind", (int)kind), ("$oldest", oldestHistoryId));
                retainedCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var priorityCount = Command(connection, transaction,
                "SELECT COUNT(*) FROM message_records WHERE room_tab_id=$room AND kind=$kind AND priority=1;",
                ("$room", roomTabId), ("$kind", (int)kind));
            var pinnedCount = Convert.ToInt32(priorityCount.ExecuteScalar(), CultureInfo.InvariantCulture);
            var unreadCount = 0;
            if (kind != MessageKind.Danmu)
            {
                using var unread = Command(connection, transaction, """
                    SELECT COUNT(*) FROM message_records
                    WHERE room_tab_id=$room AND kind=$kind AND json_extract(payload,'$.IsRead')=0;
                    """, ("$room", roomTabId), ("$kind", (int)kind));
                unreadCount = Convert.ToInt32(unread.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            var totalLimit = pinnedCount + Math.Max(retainedCount, historyLimit);
            using var command = Command(connection, transaction, """
                SELECT payload FROM message_records WHERE room_tab_id=$room AND kind=$kind
                ORDER BY priority DESC,pinned_ms DESC,occurred_ms DESC,sequence DESC LIMIT $limit;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", totalLimit));
            var records = ReadMessages(command);
            transaction.Commit();
            return new MessageFeedSnapshot(records, unreadCount);
        }, cancellationToken);
    }
}
