namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>当前全局礼物阅读规则，价格单位为人民币元。</summary>
public sealed record MessageReadOptions
{
    /// <summary>礼物列表显示的最低累计金额，达到门槛的未读礼物优先显示。</summary>
    public decimal GiftAutoPinMinimum { get; init; }
    public GiftReadResetPolicy GiftReadResetPolicy { get; init; } = GiftReadResetPolicy.ConfirmOnce;
}
