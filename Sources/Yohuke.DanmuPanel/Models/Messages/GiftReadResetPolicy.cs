namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>礼物连击新增数量后的已读规则。</summary>
public enum GiftReadResetPolicy
{
    ConfirmOnce,
    NewQuantity,
    NewValueThreshold
}
