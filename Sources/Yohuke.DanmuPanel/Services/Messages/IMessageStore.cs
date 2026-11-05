using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

/// <summary>消息、阅读状态和译文的持久化边界。</summary>
public interface IMessageStore : IAsyncDisposable
{
    /// <summary>单次消息追加或翻译任务登记允许处理的最大记录数。</summary>
    public const int MAX_BATCH_COUNT = 64;

    /// <summary>提交消息、连击和可选翻译任务，返回已提交的可见变化。</summary>
    Task<MessageAppendResult> AppendAsync(MessageRecord message, MessageReadOptions options,
        string? translationFingerprint = null, CancellationToken cancellationToken = default);
    /// <summary>以单个事务顺序提交有界消息批次，返回与输入逐条对应的已提交结果。</summary>
    Task<IReadOnlyList<MessageAppendResult>> AppendBatchAsync(IReadOnlyList<MessageRecord> messages, MessageReadOptions options,
        string? translationFingerprint = null, CancellationToken cancellationToken = default);
    /// <summary>按置顶、未读优先规则分页读取消息。</summary>
    Task<IReadOnlyList<MessageRecord>> LoadPageAsync(string roomTabId, MessageKind kind, int offset, int limit,
        CancellationToken cancellationToken = default);
    /// <summary>按原始时间读取近期记录，供跨会话补拉去重。</summary>
    Task<IReadOnlyList<MessageRecord>> LoadRecentAsync(string roomTabId, MessageKind kind, int limit,
        CancellationToken cancellationToken = default);
    /// <summary>读取仍然存在的消息。</summary>
    Task<MessageRecord?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>提交普通弹幕置顶状态的切换。</summary>
    Task<MessageRecord?> TogglePinAsync(string id, CancellationToken cancellationToken = default);
    /// <summary>仅确认用户操作时实际看到的数量和价值。</summary>
    Task<MessageRecord?> MarkReadAsync(string id, int observedQuantity, decimal observedTotalPrice,
        CancellationToken cancellationToken = default);
    /// <summary>在同一事务中确认指定房间与消息类型的全部未读记录，返回确认条数。</summary>
    Task<int> MarkAllReadAsync(string roomTabId, MessageKind kind, CancellationToken cancellationToken = default);
    /// <summary>清空指定房间和类型的全部记录及翻译任务，保留其他类型和礼物连击增量基线。</summary>
    Task ClearFeedAsync(string roomTabId, MessageKind kind, CancellationToken cancellationToken = default);
    /// <summary>清空房间消息并使旧代次的回调失效。</summary>
    Task ClearRoomAsync(string roomTabId, CancellationToken cancellationToken = default);
    /// <summary>清空全部已登记房间并推进各房间代次。</summary>
    Task ClearAllAsync(CancellationToken cancellationToken = default);
    /// <summary>仅为当前代次、当前翻译配置的现存消息提交译文。</summary>
    Task<bool> ApplyTranslationAsync(string id, long generation, string fingerprint, string translation,
        CancellationToken cancellationToken = default);
    /// <summary>登记房间并读取监听回调应携带的存储代次。</summary>
    Task<long> GetGenerationAsync(string roomTabId, CancellationToken cancellationToken = default);
    /// <summary>保存阅读设置并重新计算礼物优先级，不清除已读确认。</summary>
    Task ReevaluateReadOptionsAsync(MessageReadOptions options, CancellationToken cancellationToken = default);
    /// <summary>为可见或手动重试的现存消息建立当前翻译任务。</summary>
    Task<bool> UpsertTranslationJobAsync(string id, long generation, string fingerprint,
        CancellationToken cancellationToken = default);
    /// <summary>按输入消息的 ID 和代次校验现存记录，在一个事务中登记翻译任务并返回成功登记的 ID。</summary>
    Task<IReadOnlySet<string>> UpsertTranslationJobsAsync(IReadOnlyList<MessageRecord> messages, string fingerprint,
        CancellationToken cancellationToken = default);
}
