namespace Yohuke.DanmuPanel.Views.Components;

internal readonly record struct RenderedMessageTextSpan(int SourceStart, int TextStart, int TextLength, bool IsEmoticon);
