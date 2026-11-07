using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证礼物累计价值色阶与舰长等级配色。</summary>
public sealed class MessageCardColorTests
{
    /// <summary>累计金额的各个边界使用对应价值色阶，已读不会改变价值颜色。</summary>
    [Theory]
    [InlineData("0", "#8B9BA8")]
    [InlineData("0.99", "#8B9BA8")]
    [InlineData("1", "#65B87A")]
    [InlineData("9.99", "#65B87A")]
    [InlineData("10", "#39B5C8")]
    [InlineData("29.99", "#39B5C8")]
    [InlineData("30", "#3171D2")]
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
        var record = new MessageRecord { Kind = MessageKind.Gift, UnitPrice = 0.1m, TotalPrice = 0 };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor("#8B9BA8", item.BorderBrush);
        item.Update(record with { TotalPrice = 1 }, settings);
        AssertColor("#65B87A", item.BorderBrush);
        item.Update(record with { TotalPrice = 10 }, settings);
        AssertColor("#39B5C8", item.BorderBrush);
        item.Update(record with { TotalPrice = 30 }, settings);
        AssertColor("#3171D2", item.BorderBrush);
        settings.ColorScheme.Overrides["GiftValue100Brush"] = "#123456";
        item.Update(record with { TotalPrice = 100 }, settings);
        AssertColor("#123456", item.BorderBrush);
        settings.ColorScheme.Overrides.Clear();
        item.Update(item.Record, settings);
        AssertColor("#E2B52B", item.BorderBrush);
    }

    /// <summary>低价连击按累计金额跨档，独立覆盖及恢复均作用于当前档位。</summary>
    [Theory]
    [InlineData("0", "GiftValueUnder1Brush", "#8B9BA8")]
    [InlineData("1", "GiftValue1Brush", "#65B87A")]
    [InlineData("10", "GiftValue10Brush", "#39B5C8")]
    [InlineData("30", "GiftValueUnder50Brush", "#3171D2")]
    public void LowPriceGiftTiersUpdateIndependently(string amount, string key, string expected)
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { Kind = MessageKind.Gift, UnitPrice = 0.1m, TotalPrice = 50 };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor("#427D9E", item.BorderBrush);
        settings.ColorScheme.Overrides[key] = "#AB12CD";
        item.Update(record, settings);
        AssertColor("#427D9E", item.BorderBrush);
        item.Update(record with { TotalPrice = decimal.Parse(amount, CultureInfo.InvariantCulture) }, settings);
        AssertColor("#AB12CD", item.BorderBrush);
        settings.ColorScheme.Overrides.Clear();
        item.Update(item.Record, settings);
        AssertColor(expected, item.BorderBrush);
    }

    /// <summary>低价档与原有档位的独立配色可随命名方案保存并恢复。</summary>
    [Fact]
    public void GiftPriceTierColorsRoundTripInSavedSchemes()
    {
        var keys = new[]
        {
            "GiftValueUnder1Brush", "GiftValue1Brush", "GiftValue10Brush", "GiftValueUnder50Brush",
            "GiftValue50Brush", "GiftValue100Brush", "GiftValue500Brush", "GiftValue1000Brush", "GiftValue2000Brush"
        };
        var settings = new ColorSchemeSettings
        {
            Overrides = keys.ToDictionary(key => key, _ => "#80123456"),
            SavedSchemes = [new() { Id = "gift", Name = "Gift", Colors = keys.ToDictionary(key => key, _ => "#654321") }],
            SelectedSchemeId = "gift"
        };
        ColorSchemeService.Validate(settings);
        var restored = JsonSerializer.Deserialize<ColorSchemeSettings>(JsonSerializer.Serialize(settings))!;
        ColorSchemeService.Validate(restored);
        Assert.All(keys, key =>
        {
            Assert.Equal("#80123456", restored.Overrides[key]);
            Assert.Equal("#654321", Assert.Single(restored.SavedSchemes).Colors[key]);
        });
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

    /// <summary>舰队边框与同档粉丝牌底色分别覆盖和恢复，任意一侧的修改都不影响另一侧。</summary>
    [Theory]
    [InlineData(3, "GuardCaptainBrush", "MedalCaptainBackgroundBrush", "#4F8FDD")]
    [InlineData(2, "GuardAdmiralBrush", "MedalAdmiralBackgroundBrush", "#A46EE8")]
    [InlineData(1, "GuardGovernorBrush", "MedalGovernorBackgroundBrush", "#E9A23B")]
    public void GuardBordersAndMedalBackgroundsAreIndependent(int level, string borderKey, string medalKey, string defaultColor)
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { Kind = MessageKind.Guard, GuardLevel = level, MedalGuardLevel = level, IsRead = true };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor(defaultColor, item.BorderBrush);
        AssertColor(defaultColor, item.MedalBackgroundBrush);

        settings.ColorScheme.Overrides[borderKey] = "#123456";
        item.Update(record, settings);
        AssertColor("#123456", item.BorderBrush);
        AssertColor(defaultColor, item.MedalBackgroundBrush);

        settings.ColorScheme.Overrides[medalKey] = "#FEDCBA";
        item.Update(record, settings);
        AssertColor("#123456", item.BorderBrush);
        AssertColor("#FEDCBA", item.MedalBackgroundBrush);

        settings.ColorScheme.Overrides.Remove(borderKey);
        item.Update(record, settings);
        AssertColor(defaultColor, item.BorderBrush);
        AssertColor("#FEDCBA", item.MedalBackgroundBrush);

        settings.ColorScheme.Overrides.Remove(medalKey);
        item.Update(record, settings);
        AssertColor(defaultColor, item.MedalBackgroundBrush);
    }

    private static void AssertColor(string expected, IBrush brush)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }
}
