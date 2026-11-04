using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed record EmoticonTextSegment(int SourceStart, string Text, DanmuEmoticon? Emoticon);
