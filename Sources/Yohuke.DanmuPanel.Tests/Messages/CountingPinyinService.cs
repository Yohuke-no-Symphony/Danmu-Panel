using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Messages;

internal sealed class CountingPinyinService : IPinyinService
{
    private readonly PinyinService service = new();
    internal int AnnotationCalls { get; private set; }

    /// <inheritdoc/>
    public string GetPinyin(string text)
    {
        return service.GetPinyin(text);
    }

    /// <inheritdoc/>
    public IReadOnlyList<PinyinAnnotation> GetAnnotations(string text)
    {
        AnnotationCalls++;
        return service.GetAnnotations(text);
    }
}
