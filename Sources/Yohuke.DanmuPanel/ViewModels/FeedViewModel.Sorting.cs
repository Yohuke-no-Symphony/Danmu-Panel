using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private MessageSortOrder sortOrder;

    /// <summary>当前面板和阅读标签正在使用的排列方向。</summary>
    public MessageSortOrder SortOrder => sortOrder;

    private bool UpdateSortOrder()
    {
        return SetProperty(ref sortOrder, runtime.Configuration.Settings.MessageSorting.GetOrder(kind, ReadFilter), nameof(SortOrder));
    }
}
