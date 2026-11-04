using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

/// <summary>本地测试消息的原子写入与定向清理边界。</summary>
public interface ITestMessageStore
{
    /// <summary>将同一房间的消息及可选翻译任务作为单次事务提交，复用普通消息的调度规则。</summary>
    Task<int> AppendTestMessagesAsync(IReadOnlyList<MessageRecord> messages, MessageReadOptions options,
        string? translationFingerprint = null, CancellationToken cancellationToken = default);

    /// <summary>仅删除指定房间的测试记录，不推进真实消息代次。</summary>
    Task<int> ClearTestMessagesAsync(string roomTabId, CancellationToken cancellationToken = default);
}
