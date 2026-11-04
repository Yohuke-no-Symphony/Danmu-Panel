namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>当前全局礼物阅读规则，价格单位为人民币元。</summary>
public sealed record MessageReadOptions
{
    public decimal GiftAutoPinMinimum { get; init; }
    public GiftReadResetPolicy GiftReadResetPolicy { get; init; } = GiftReadResetPolicy.ConfirmOnce;
}
