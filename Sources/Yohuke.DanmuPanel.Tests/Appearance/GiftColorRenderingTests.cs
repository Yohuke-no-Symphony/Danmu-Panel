using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证低价礼物卡片的默认颜色与运行中的独立配色更新。</summary>
public sealed class GiftColorRenderingTests
{
    /// <summary>普通和紧凑布局都按累计金额显示低价档，已有卡片可独立改色并恢复。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCardsRenderAndRefreshIndependentLowPriceTierBorders(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var expected = new Dictionary<decimal, string>
        {
            [0] = "#8B9BA8", [1] = "#65B87A", [10] = "#39B5C8", [30] = "#3171D2", [50] = "#427D9E"
        };
        foreach (var price in expected.Keys)
        {
            await runtime.Messages.AppendAsync(new()
            {
                RoomTabId = room.Id, RoomId = room.RoomId, Kind = MessageKind.Gift,
                UserName = "观众", GiftName = "礼物", UnitPrice = price == 0 ? 0 : 0.1m,
                Quantity = Math.Max(1, (int)(price / 0.1m)), TotalPrice = price
            }, new());
        }
        var view = new MessageFeedView(runtime, MessageKind.Gift, exception => throw exception);
        var window = new Window { Width = 760, Height = 820, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            window.Show();
            window.UpdateLayout();
            var cards = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("messageCard"))
                .ToDictionary(border => Assert.IsType<MessageItemViewModel>(border.DataContext).Record.TotalPrice);
            Assert.Equal(expected.Count, cards.Count);
            foreach (var (price, color) in expected)
            {
                AssertColor(color, cards[price].BorderBrush);
            }
            settings.ColorScheme.Overrides["GiftValueUnder1Brush"] = "#AB12CD";
            settings.ColorScheme.Overrides["GiftValue1Brush"] = "#123456";
            settings.ColorScheme.Overrides["GiftValue10Brush"] = "#654321";
            await view.Model.RefreshAsync();
            AssertColor("#AB12CD", cards[0].BorderBrush);
            AssertColor("#123456", cards[1].BorderBrush);
            AssertColor("#654321", cards[10].BorderBrush);
            AssertColor(expected[30], cards[30].BorderBrush);
            AssertColor(expected[50], cards[50].BorderBrush);
            settings.ColorScheme.Overrides.Clear();
            await view.Model.RefreshAsync();
            foreach (var (price, color) in expected)
            {
                AssertColor(color, cards[price].BorderBrush);
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void AssertColor(string expected, IBrush? brush)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }
}
