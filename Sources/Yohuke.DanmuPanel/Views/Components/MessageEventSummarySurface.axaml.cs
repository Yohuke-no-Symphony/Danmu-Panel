using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageEventSummarySurface : Border
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>创建普通弹幕中的付费事件摘要。</summary>
    public MessageEventSummarySurface()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
