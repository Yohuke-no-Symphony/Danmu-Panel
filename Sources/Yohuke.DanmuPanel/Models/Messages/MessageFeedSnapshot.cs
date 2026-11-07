namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>同一数据库快照中的消息窗口、翻译待办与该房间、类型的完整未读条数。</summary>
public sealed record MessageFeedSnapshot(IReadOnlyList<MessageRecord> Records, int UnreadCount)
{
    /// <summary>当前窗口最旧的普通历史记录，与展示排序方向无关。</summary>
    public string? OldestHistoryId { get; init; }

    public IReadOnlySet<string> PendingTranslationIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
