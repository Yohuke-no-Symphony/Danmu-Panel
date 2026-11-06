using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>将全局紧凑与换行偏好应用到消息面板的样式作用域。</summary>
public sealed partial class MessageFeedSurface : UserControl
{
    private MessageFeedViewportAlignment? viewportAlignment;

    /// <summary>加载消息面板显示容器。</summary>
    public MessageFeedSurface()
    {
        InitializeComponent();
    }

    internal ContentControl Host => this.FindControl<ContentControl>("FeedHost")!;
    internal ContentControl PinnedContainer => this.FindControl<ContentControl>("PinnedHost")!;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (PinnedContainer.Content is ListBox pinnedList && Host.Content is ListBox scrollingList)
        {
            viewportAlignment = new(pinnedList, scrollingList);
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        viewportAlignment?.Dispose();
        viewportAlignment = null;
        base.OnDetachedFromVisualTree(e);
    }
}
