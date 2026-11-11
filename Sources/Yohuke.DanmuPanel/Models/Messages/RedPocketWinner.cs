namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>红包中奖用户及其获得的奖品或电池。</summary>
public sealed record RedPocketWinner
{
    public long Uid { get; init; }
    public string UserName { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public bool IsMystery { get; init; }
    public string GiftId { get; init; } = "";
    public string GiftName { get; init; } = "";
    public string GiftImageUrl { get; init; } = "";
    public int Quantity { get; init; } = 1;
    public int? AwardType { get; init; }
    public decimal? AwardPrice { get; init; }
    public decimal? BatteryAmount { get; init; }
    public decimal? IntimacyAmount { get; init; }
}
