using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>执行与具体服务商无关的单条文本翻译。</summary>
public interface ITranslationClient
{
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default);
}
