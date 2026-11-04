using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>将全局紧凑与换行偏好应用到消息面板的样式作用域。</summary>
public sealed partial class MessageFeedSurface : UserControl
{
    /// <summary>加载消息面板显示容器。</summary>
    public MessageFeedSurface()
    {
        InitializeComponent();
    }

    internal ContentControl Host => this.FindControl<ContentControl>("FeedHost")!;
    internal ContentControl PinnedContainer => this.FindControl<ContentControl>("PinnedHost")!;
}
