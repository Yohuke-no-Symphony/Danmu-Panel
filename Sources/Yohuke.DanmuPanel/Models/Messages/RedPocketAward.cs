namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>红包中的一项奖品及其协议价值。</summary>
public sealed record RedPocketAward
{
    public string GiftId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ImageUrl { get; init; } = "";
    public int? Quantity { get; init; }
    public int? Type { get; init; }
    public decimal? Price { get; init; }
}
