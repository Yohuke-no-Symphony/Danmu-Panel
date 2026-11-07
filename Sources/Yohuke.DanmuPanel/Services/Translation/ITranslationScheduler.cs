using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>调度持久翻译任务并控制并发与积压。</summary>
public interface ITranslationScheduler : IAsyncDisposable
{
    event EventHandler<TranslationCompletedEventArgs>? Completed;
    event Action<string>? Error;
    event Action<string, MessageKind?>? PendingChanged;
    Task StartAsync(TranslationSettings settings, CancellationToken cancellationToken = default,
        DanmuRegexFilter? danmuFilter = null);
    Task UpdateSettingsAsync(TranslationSettings settings, CancellationToken cancellationToken = default,
        DanmuRegexFilter? danmuFilter = null);
    void NotifyWorkAvailable(bool retryAuthentication = false);
}
