using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private MessageReadFilter readFilter;

    /// <summary>当前面板独立持有的阅读状态筛选，切换房间时保持。</summary>
    public MessageReadFilter ReadFilter => readFilter;

    /// <summary>标题阅读标签的选择位置。</summary>
    public int SelectedReadTabIndex => readFilter == MessageReadFilter.Read ? 1 : 0;

    /// <summary>切换未读或已读窗口，取消旧请求并从当前分页上限重新加载。</summary>
    public async Task SelectReadFilterAsync(MessageReadFilter selected)
    {
        if (disposed || kind == MessageKind.Danmu || readFilter == selected)
        {
            return;
        }
        if (selected is not (MessageReadFilter.Unread or MessageReadFilter.Read))
        {
            throw new ArgumentOutOfRangeException(nameof(selected));
        }

        SetProperty(ref readFilter, selected, nameof(ReadFilter));
        ResetWindow();
        OnPropertyChanged(nameof(SelectedReadTabIndex));
        await RefreshAsync();
    }
}
