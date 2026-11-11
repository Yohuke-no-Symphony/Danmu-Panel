using System.Globalization;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>红包内奖品及协议明确提供的整包数量。</summary>
public sealed class RedPocketAwardViewModel
{
    public string Name { get; }
    public string Quantity { get; }
    public bool HasQuantity => Quantity.Length > 0;

    /// <summary>为尚未提供奖品总数的开奖结果保留空数量。</summary>
    public RedPocketAwardViewModel(RedPocketAward award)
    {
        Name = award.Name;
        Quantity = award.Quantity is { } quantity ? "×" + quantity.ToString(CultureInfo.CurrentCulture) : "";
    }
}
