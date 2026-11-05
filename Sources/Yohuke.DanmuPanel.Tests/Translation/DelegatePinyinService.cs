using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

internal sealed class DelegatePinyinService(Func<string, string> convert,
    Func<string, IReadOnlyList<PinyinAnnotation>>? annotate = null) : IPinyinService
{
    /// <inheritdoc />
    public string GetPinyin(string text)
    {
        return convert(text);
    }

    /// <inheritdoc />
    public IReadOnlyList<PinyinAnnotation> GetAnnotations(string text)
    {
        return annotate?.Invoke(text) ?? [];
    }
}
