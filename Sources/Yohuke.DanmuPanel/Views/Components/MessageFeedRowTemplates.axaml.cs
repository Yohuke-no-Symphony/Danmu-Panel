using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageFeedRowTemplates : ResourceDictionary
{
    public MessageFeedRowTemplates()
    {
        AvaloniaXamlLoader.Load(this);
    }

    internal Control Build(MessageKind kind, bool isEventSummary = false)
    {
        var key = isEventSummary ? "DanmuSummary" : kind.ToString();
        var result = ((IDataTemplate)this[key]!).Build(null)!;
        result.ClearValue(Control.DataContextProperty);
        return result;
    }
}
