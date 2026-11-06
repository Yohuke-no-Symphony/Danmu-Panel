using System.Text.Json.Serialization;

namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>具有用户信息和正文分栏的消息类型各自独立的列宽设置。</summary>
public sealed class MessageColumnSettings
{
    public MessageColumnWidthSettings Danmu { get; set; } = new();
    public MessageColumnWidthSettings Gift { get; set; } = new();

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public MessageColumnWidthSettings Guard { get; set; } = new()
    {
        RegularIdentityRatio = 0.6,
        CompactIdentityRatio = 0.6
    };

    internal MessageColumnWidthSettings GetWidths(MessageKind kind)
    {
        return kind switch
        {
            MessageKind.Danmu => Danmu,
            MessageKind.Gift => Gift,
            MessageKind.Guard => Guard,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The message kind has no horizontal columns.")
        };
    }
}
