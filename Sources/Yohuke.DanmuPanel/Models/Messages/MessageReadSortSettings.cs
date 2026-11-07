namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>一个付费消息面板的未读与已读排序偏好。</summary>
public sealed record MessageReadSortSettings
{
    public MessageSortOrder Unread { get; init; } = MessageSortOrder.OldestFirst;
    public MessageSortOrder Read { get; init; } = MessageSortOrder.NewestFirst;
}
