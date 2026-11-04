using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

internal sealed class DelegateTranslationClient(Func<TranslationRequest, CancellationToken, Task<string>> translate) : ITranslationClient
{
    public Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        return translate(request, cancellationToken);
    }
}
