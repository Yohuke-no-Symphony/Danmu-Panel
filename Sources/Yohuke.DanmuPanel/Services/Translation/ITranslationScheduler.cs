using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>调度持久翻译任务并控制并发与积压。</summary>
public interface ITranslationScheduler : IAsyncDisposable
{
    event EventHandler<TranslationCompletedEventArgs>? Completed;
    event Action<string>? Error;
    event Action<string>? PendingChanged;
    Task StartAsync(TranslationSettings settings, CancellationToken cancellationToken = default);
    Task UpdateSettingsAsync(TranslationSettings settings, CancellationToken cancellationToken = default);
    void NotifyWorkAvailable(bool retryAuthentication = false);
}
