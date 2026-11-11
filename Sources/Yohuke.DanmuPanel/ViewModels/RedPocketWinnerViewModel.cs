using System.Globalization;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>中奖用户及其奖品的本地化显示投影。</summary>
public sealed class RedPocketWinnerViewModel
{
    public long UserUid { get; }
    public string Name { get; }
    public string Award { get; }

    /// <summary>保留中奖身份，按奖品类型生成数量文本。</summary>
    public RedPocketWinnerViewModel(RedPocketWinner winner)
    {
        UserUid = winner.IsMystery ? 0 : winner.Uid;
        Name = winner.IsMystery
            ? Localization.Get("RedPocket.MysteryUser")
            : string.IsNullOrWhiteSpace(winner.UserName)
                ? winner.Uid.ToString(CultureInfo.CurrentCulture)
                : winner.UserName;
        Award = FormatAward(winner);
    }

    private static string FormatAward(RedPocketWinner winner)
    {
        if (winner.BatteryAmount is { } battery)
        {
            return Localization.Format("RedPocket.BatteryValue", battery / 100m);
        }
        if (winner.IntimacyAmount is { } intimacy)
        {
            return Localization.Format("RedPocket.IntimacyValue", intimacy);
        }
        if (!string.IsNullOrWhiteSpace(winner.GiftName))
        {
            return Localization.Format("RedPocket.AwardQuantity", winner.GiftName, winner.Quantity);
        }
        return Localization.Get("RedPocket.AwardPending");
    }
}
