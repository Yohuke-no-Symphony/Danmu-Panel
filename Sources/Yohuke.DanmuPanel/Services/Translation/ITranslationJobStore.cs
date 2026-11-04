using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>与消息共用事务的持久翻译任务存储。</summary>
public interface ITranslationJobStore
{
    Task<IReadOnlyList<TranslationJob>> GetPendingAsync(int limit, CancellationToken cancellationToken = default);
    Task<bool> CompleteAsync(TranslationJob job, string translation, CancellationToken cancellationToken = default);
    Task<bool> FailAsync(TranslationJob job, string error, CancellationToken cancellationToken = default);
    Task ResetPendingAsync(string configurationFingerprint, CancellationToken cancellationToken = default);
    /// <summary>跳过过期或超出等待上限的普通任务；在途任务不参与清理及等待数量统计。</summary>
    Task<int> TrimPendingAsync(DateTimeOffset olderThan, int maxOrdinary, IReadOnlyCollection<string> inFlightJobIds,
        CancellationToken cancellationToken = default);
}
