using System.Globalization;
using Microsoft.Data.Sqlite;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    /// <summary>在同一读取事务中取得完整未读条数和消息窗口，并保留历史边界。</summary>
    public Task<MessageFeedSnapshot> LoadFeedAsync(string roomTabId, MessageKind kind, int historyLimit,
        string? oldestHistoryId, CancellationToken cancellationToken = default, bool hideEmoticonDanmu = false,
        DanmuRegexFilter? danmuRegexFilter = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyLimit);
        return EnqueueAsync(connection =>
        {
            var matches = new Dictionary<string, bool>(StringComparer.Ordinal);
            connection.CreateFunction<string, bool>("is_regex_blocked", content =>
            {
                if (!matches.TryGetValue(content, out var blocked))
                {
                    blocked = danmuRegexFilter?.IsBlocked(content) == true;
                    matches.Add(content, blocked);
                }
                return blocked;
            });
            try
            {
                return ReadFeed(connection, roomTabId, kind, historyLimit, oldestHistoryId,
                    hideEmoticonDanmu && kind == MessageKind.Danmu,
                    danmuRegexFilter is { Enabled: true } && kind == MessageKind.Danmu);
            }
            finally
            {
                connection.CreateFunction<string, bool>("is_regex_blocked", null);
            }
        }, cancellationToken);
    }

    private MessageFeedSnapshot ReadFeed(SqliteConnection connection, string roomTabId, MessageKind kind,
        int historyLimit, string? oldestHistoryId, bool hideEmoticons, bool hideByRegex)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var retainedCount = 0;
        if (oldestHistoryId is not null)
        {
            using var count = Command(connection, transaction, """
                SELECT COUNT(*) FROM message_records
                WHERE room_tab_id=$room AND kind=$kind AND priority=0
                    AND ($hide=0 OR is_emoticon_only(json_extract(payload,'$.Content'),
                        COALESCE(json_extract(payload,'$.Emoticons'),'[]'))=0)
                    AND ($regex=0 OR is_regex_blocked(json_extract(payload,'$.Content'))=0)
                    AND (occurred_ms,sequence)>=(
                        SELECT occurred_ms,sequence FROM message_records WHERE id=$oldest);
                """, ("$room", roomTabId), ("$kind", (int)kind), ("$oldest", oldestHistoryId),
                ("$hide", hideEmoticons), ("$regex", hideByRegex));
            retainedCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using var priorityCount = Command(connection, transaction, """
            SELECT COUNT(*) FROM message_records WHERE room_tab_id=$room AND kind=$kind AND priority=1
                AND ($hide=0 OR is_emoticon_only(json_extract(payload,'$.Content'),
                    COALESCE(json_extract(payload,'$.Emoticons'),'[]'))=0)
                AND ($regex=0 OR is_regex_blocked(json_extract(payload,'$.Content'))=0);
            """, ("$room", roomTabId), ("$kind", (int)kind), ("$hide", hideEmoticons), ("$regex", hideByRegex));
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
            SELECT messages.id,messages.payload,jobs.status FROM (
                SELECT id,generation,payload,priority,occurred_ms,sequence FROM message_records
                WHERE room_tab_id=$room AND kind=$kind
                    AND ($hide=0 OR is_emoticon_only(json_extract(payload,'$.Content'),
                        COALESCE(json_extract(payload,'$.Emoticons'),'[]'))=0)
                    AND ($regex=0 OR is_regex_blocked(json_extract(payload,'$.Content'))=0)
                ORDER BY priority DESC,pinned_ms DESC,occurred_ms DESC,sequence DESC LIMIT $limit
            ) AS messages
            LEFT JOIN translation_jobs AS jobs ON jobs.id=messages.id AND jobs.generation=messages.generation
                AND jobs.fingerprint=json_extract(messages.payload,'$.TranslationFingerprint')
            ORDER BY messages.priority DESC,messages.occurred_ms ASC,messages.sequence ASC;
            """, ("$room", roomTabId), ("$kind", (int)kind), ("$limit", totalLimit),
            ("$hide", hideEmoticons), ("$regex", hideByRegex));
        var records = new List<MessageRecord>();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var record = readCache.GetOrDeserialize(reader.GetString(0), reader.GetString(1));
                records.Add(record);
                if (!reader.IsDBNull(2) && reader.GetInt32(2) == 0)
                {
                    pending.Add(record.Id);
                }
            }
        }
        transaction.Commit();
        return new MessageFeedSnapshot(records, unreadCount) { PendingTranslationIds = pending };
    }
}
