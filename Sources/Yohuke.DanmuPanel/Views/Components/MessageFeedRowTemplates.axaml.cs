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

    internal Control Build(MessageKind kind)
    {
        var result = ((IDataTemplate)this[kind.ToString()]!).Build(null)!;
        result.ClearValue(Control.DataContextProperty);
        return result;
    }
}
