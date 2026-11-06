namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>扣除固定粉丝牌及列间距后，用户信息在两栏可分配宽度中的占比。</summary>
public sealed class MessageColumnWidthSettings
{
    /// <summary>宽松布局下的用户信息列宽比例。</summary>
    public double RegularIdentityRatio { get; set; } = 0.4;

    /// <summary>紧凑布局下的用户信息列宽比例。</summary>
    public double CompactIdentityRatio { get; set; } = 0.4;
}
