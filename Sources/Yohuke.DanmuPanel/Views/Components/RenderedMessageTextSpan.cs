namespace Yohuke.DanmuPanel.Views.Components;

internal sealed record RenderedMessageTextSpan(int SourceStart, int TextStart, int TextLength, bool IsEmoticon);
