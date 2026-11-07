namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>分别保存 SC、礼物和大航海面板的阅读排序。</summary>
public sealed record MessageSortSettings
{
    public MessageReadSortSettings SuperChat { get; init; } = new();
    public MessageReadSortSettings Gift { get; init; } = new();
    public MessageReadSortSettings Guard { get; init; } = new();

    /// <summary>取得指定付费消息面板的独立排序偏好。</summary>
    public MessageReadSortSettings GetPanel(MessageKind kind)
    {
        return kind switch
        {
            MessageKind.SuperChat => SuperChat,
            MessageKind.Gift => Gift,
            MessageKind.Guard => Guard,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>取得当前阅读标签的排序，普通弹幕和未分组查询沿用旧到新。</summary>
    public MessageSortOrder GetOrder(MessageKind kind, MessageReadFilter filter)
    {
        if (kind == MessageKind.Danmu || filter == MessageReadFilter.All)
        {
            return MessageSortOrder.OldestFirst;
        }
        var panel = GetPanel(kind);
        return filter switch
        {
            MessageReadFilter.Unread => panel.Unread,
            MessageReadFilter.Read => panel.Read,
            _ => throw new ArgumentOutOfRangeException(nameof(filter))
        };
    }
}
