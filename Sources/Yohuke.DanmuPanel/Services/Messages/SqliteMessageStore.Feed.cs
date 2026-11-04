using System.Globalization;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    /// <summary>读取全部置顶消息和普通消息窗口，并保留此前加载到的历史边界。</summary>
    public Task<IReadOnlyList<MessageRecord>> LoadFeedAsync(string roomTabId, MessageKind kind, int historyLimit,
        string? oldestHistoryId, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        return EnqueueAsync<IReadOnlyList<MessageRecord>>(connection =>
        {
            var retainedCount = 0;
            if (oldestHistoryId is not null)
            {
                using var count = Command(connection, null, """
                    SELECT COUNT(*) FROM message_records
                    WHERE room_tab_id=$room AND kind=$kind AND priority=0
                        AND (occurred_ms,sequence)>=(
                            SELECT occurred_ms,sequence FROM message_records WHERE id=$oldest);
                    """, ("$room", roomTabId), ("$kind", (int)kind), ("$oldest", oldestHistoryId));
                retainedCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var priorityCount = Command(connection, null,
                "SELECT COUNT(*) FROM message_records WHERE room_tab_id=$room AND kind=$kind AND priority=1;",
                ("$room", roomTabId), ("$kind", (int)kind));
            var pinnedCount = Convert.ToInt32(priorityCount.ExecuteScalar(), CultureInfo.InvariantCulture);
            var totalLimit = pinnedCount + Math.Max(retainedCount, historyLimit);
            using var command = Command(connection, null, """
                SELECT payload FROM message_records WHERE room_tab_id=$room AND kind=$kind
                ORDER BY priority DESC,pinned_ms DESC,occurred_ms DESC,sequence DESC LIMIT $limit;
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", totalLimit));
            return ReadMessages(command);
        }, cancellationToken);
    }
}
