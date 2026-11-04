using System.Globalization;
using System.Text.Json;
using Avalonia.Media;
using Xunit;
using Yohuke.DanmuPanel.Models.Appearance;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证 SC 金额分色、测试消息持久化及独立档位覆盖。</summary>
public sealed class SuperChatColorTests
{
    /// <summary>无上游配色或旧版固定蓝色的测试消息，均按金额边界显示。</summary>
    [Theory]
    [InlineData("0", "#3171D2")]
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
    public void MissingColorsAndLegacyTestMessagesUsePriceTier(string amount, string expected)
    {
        var settings = new PanelSettings();
        var record = new MessageRecord
        {
            Kind = MessageKind.SuperChat,
            TotalPrice = decimal.Parse(amount, CultureInfo.InvariantCulture)
        };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor(expected, item.BorderBrush);
        item.Update(record with { IsTest = true, BorderColor = "#1D71B8", IsRead = true }, settings);
        AssertColor(expected, item.BorderBrush);
    }

    /// <summary>按档位覆盖优先于旧版统一配色，其他档位保持原值，重置恢复上游颜色。</summary>
    [Fact]
    public void TierOverrideIsIndependentAndTakesPriorityOverLegacySharedColor()
    {
        var settings = new PanelSettings();
        var record = new MessageRecord { Kind = MessageKind.SuperChat, TotalPrice = 100, BorderColor = "#123456" };
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor("#123456", item.BorderBrush);
        settings.ColorScheme.Overrides["SuperChatBorderBrush"] = "#AABBCC";
        settings.ColorScheme.Overrides["SuperChatValue100Brush"] = "#80654321";
        item.Update(record, settings);
        AssertColor("#80654321", item.BorderBrush);
        item.Update(record with { TotalPrice = 500 }, settings);
        AssertColor("#AABBCC", item.BorderBrush);
        settings.ColorScheme.Overrides.Clear();
        item.Update(record, settings);
        AssertColor("#123456", item.BorderBrush);
        Assert.Equal("#123456", item.Record.BorderColor);
    }

    /// <summary>测试消息写入对应档位色，重新加载和显示时不回退到统一蓝色。</summary>
    [Theory]
    [InlineData(30, "#3171D2")]
    [InlineData(50, "#427D9E")]
    [InlineData(100, "#E2B52B")]
    [InlineData(500, "#E09443")]
    [InlineData(1000, "#E54D4D")]
    [InlineData(2000, "#AB1A32")]
    public async Task TestMessagePersistsItsPriceTier(int price, string expected)
    {
        using var directory = new MessageStoreTestDirectory();
        var settings = new PanelSettings();
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        await using (var store = new SqliteMessageStore(directory.DatabasePath))
        {
            var service = new TestMessageService(store, () => settings, _ => { }, () => { });
            await service.AddAsync(room, new()
            {
                Kind = MessageKind.SuperChat, UserName = "viewer", Content = "SC", UnitPrice = price
            });
        }
        await using var reopened = new SqliteMessageStore(directory.DatabasePath);
        var record = Assert.Single(await reopened.LoadPageAsync(room.Id, MessageKind.SuperChat, 0, 10));
        Assert.Equal(expected, record.BorderColor);
        AssertColor(expected, new MessageItemViewModel(record, settings, new PinyinService()).BorderBrush);
    }

    /// <summary>六个档位均可进入已保存方案，并通过配置校验与序列化恢复。</summary>
    [Fact]
    public void PriceTierColorsRoundTripInSavedSchemes()
    {
        var keys = new[]
        {
            "SuperChatValueUnder50Brush", "SuperChatValue50Brush", "SuperChatValue100Brush",
            "SuperChatValue500Brush", "SuperChatValue1000Brush", "SuperChatValue2000Brush"
        };
        var settings = new ColorSchemeSettings
        {
            Overrides = keys.ToDictionary(key => key, _ => "#80123456"),
            SavedSchemes = [new() { Id = "sc", Name = "SC", Colors = keys.ToDictionary(key => key, _ => "#654321") }],
            SelectedSchemeId = "sc"
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

    private static void AssertColor(string expected, IBrush brush)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }
}
