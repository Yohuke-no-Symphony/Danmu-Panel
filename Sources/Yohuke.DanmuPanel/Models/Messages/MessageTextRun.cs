using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Models.Messages;

internal readonly record struct MessageTextRun(
    int SourceStart,
    string Text,
    DanmuEmoticon? Emoticon,
    IReadOnlyList<PinyinAnnotation> Annotations)
{
    internal int SourceEnd => SourceStart + Text.Length;
}
