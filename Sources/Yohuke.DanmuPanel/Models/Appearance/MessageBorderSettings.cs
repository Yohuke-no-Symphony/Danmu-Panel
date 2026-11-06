using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Models.Appearance;

/// <summary>分别保存 SC、礼物与大航海消息的边框粗细。</summary>
public sealed class MessageBorderSettings
{
    /// <summary>消息边框的默认粗细。</summary>
    public const double DEFAULT_THICKNESS = 2;

    /// <summary>消息边框允许的最小粗细。</summary>
    public const double MIN_THICKNESS = 0.5;

    /// <summary>消息边框允许的最大粗细。</summary>
    public const double MAX_THICKNESS = 10;

    public double SuperChat { get; set; } = DEFAULT_THICKNESS;
    public double Gift { get; set; } = DEFAULT_THICKNESS;
    public double Guard { get; set; } = DEFAULT_THICKNESS;

    /// <summary>读取指定消息类型的边框粗细，普通弹幕不显示边框。</summary>
    public double GetThickness(MessageKind kind)
    {
        return kind switch
        {
            MessageKind.SuperChat => SuperChat,
            MessageKind.Gift => Gift,
            MessageKind.Guard => Guard,
            MessageKind.Danmu => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The message kind has no border thickness setting.")
        };
    }
}
