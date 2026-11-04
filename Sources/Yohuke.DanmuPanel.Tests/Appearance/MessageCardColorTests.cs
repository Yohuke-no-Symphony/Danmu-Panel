using System.Globalization;
using Avalonia.Media;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证礼物累计价值色阶与舰长等级配色。</summary>
public sealed class MessageCardColorTests
{
    /// <summary>累计金额的各个边界使用对应 SC 色阶，已读不会改变价值颜色。</summary>
    [Theory]
    [InlineData("0", "#3171D2")]
    [InlineData("49.99", "#3171D2")]
    [InlineData("50", "#427D9E")]
    [InlineData("99.99", "#427D9E")]
    [InlineData("100", "#E2B52B")]
    [InlineData("499.99", "#E2B52B")]
    [InlineData("500", "#E09443")]
    [InlineData("999.99", "#E09443")]
    [InlineData("1000", "#E54D4D")]
    [InlineData("1999.99", "#E54D4D")]
    [InlineData("2000", "#AB1A32")]
    [InlineData("10000", "#AB1A32")]
    public void GiftUsesTotalValueTier(string amount, string expected)
    {
        var record = new MessageRecord { Kind = MessageKind.Gift, UnitPrice = 1, TotalPrice = decimal.Parse(amount, CultureInfo.InvariantCulture) };
        var settings = new PanelSettings();
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor(expected, item.BorderBrush);
        item.Update(record with { IsRead = true }, settings);
        AssertColor(expected, item.BorderBrush);
    }

    /// <summary>连击跨档时重选颜色，覆盖值和恢复默认都作用于当前档位。</summary>
    [Fact]
    public void GiftTierUpdatesAndHonorsColorOverrides()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { Kind = MessageKind.Gift, TotalPrice = 30 };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor("#3171D2", item.BorderBrush);
        settings.ColorScheme.Overrides["GiftValue100Brush"] = "#123456";
        item.Update(record with { TotalPrice = 100 }, settings);
        AssertColor("#123456", item.BorderBrush);
        settings.ColorScheme.Overrides.Clear();
        item.Update(item.Record, settings);
        AssertColor("#E2B52B", item.BorderBrush);
    }

    /// <summary>三级上舰沿用等级颜色及用户覆盖，不受已读和金额影响。</summary>
    [Theory]
    [InlineData(3, "GuardCaptainBrush", "#4F8FDD")]
    [InlineData(2, "GuardAdmiralBrush", "#A46EE8")]
    [InlineData(1, "GuardGovernorBrush", "#E9A23B")]
    public void GuardUsesLevelAndHonorsColorOverrides(int level, string key, string expected)
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { Kind = MessageKind.Guard, GuardLevel = level, IsRead = true, TotalPrice = 2000 };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor(expected, item.BorderBrush);
        settings.ColorScheme.Overrides[key] = "#123456";
        item.Update(record, settings);
        AssertColor("#123456", item.BorderBrush);
    }

    private static void AssertColor(string expected, IBrush brush)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }
}
