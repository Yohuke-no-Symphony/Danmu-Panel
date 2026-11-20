using Avalonia.Controls.Documents;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Views.Components;

internal readonly record struct EmoticonInlineSlot(InlineCollection Inlines, int Index, DanmuEmoticon Emoticon,
    bool AlignToTextBaseline);
