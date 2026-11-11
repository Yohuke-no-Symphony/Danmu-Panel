using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Apis.Models.Bilibili;

/// <summary>携带直播间和服务器时间的红包内容或开奖更新。</summary>
public sealed record BilibiliRedPocketMessage
{
    public string RoomId { get; init; } = "";
    public long Timestamp { get; init; }
    public RedPocketData RedPocket { get; init; } = new();
}
