using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageSkeletonTemplates : ResourceDictionary
{
    public MessageSkeletonTemplates()
    {
        AvaloniaXamlLoader.Load(this);
    }

    internal Control Build(MessageKind kind)
    {
        return ((IDataTemplate)this[kind.ToString()]!).Build(null)!;
    }
}
