using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证真实 SC 卡片的六档边框与运行中配色更新。</summary>
public sealed class SuperChatColorRenderingTests
{
    /// <summary>普通和紧凑模式均显示六档颜色，已显示卡片可独立覆盖并恢复。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingCardsRenderAndRefreshIndependentPriceTierBorders(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        var room = new RoomTabState { Id = "room", RoomId = "123" };
        var expected = new Dictionary<decimal, string>
        {
            [30] = "#3171D2", [50] = "#427D9E", [100] = "#E2B52B",
            [500] = "#E09443", [1000] = "#E54D4D", [2000] = "#AB1A32"
        };
        foreach (var price in expected.Keys)
        {
            await runtime.TestMessages.AddAsync(room, new()
            {
                Kind = MessageKind.SuperChat, UserName = "测试观众", Content = $"SC 金额档位：￥{price}", UnitPrice = price
            });
        }
        var view = new MessageFeedView(runtime, MessageKind.SuperChat, exception => throw exception);
        var window = new Window { Width = 760, Height = 820, Content = view };
        var originalTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await view.Model.SelectRoomAsync(room);
            window.Show();
            window.UpdateLayout();
            var cards = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("scFeedCard"))
                .ToDictionary(border => Assert.IsType<MessageItemViewModel>(border.DataContext).Record.TotalPrice);
            Assert.Equal(6, cards.Count);
            foreach (var (price, color) in expected)
            {
                AssertColor(color, cards[price].BorderBrush);
            }
            if (Environment.GetEnvironmentVariable("DANMUPANEL_SC_SCREENSHOTS") is { Length: > 0 } screenshotDirectory)
            {
                Directory.CreateDirectory(screenshotDirectory);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(screenshotDirectory, compact ? "sc-compact.png" : "sc-normal.png"));
            }
            settings.ColorScheme.Overrides["SuperChatValue100Brush"] = "#AB12CD";
            await view.Model.RefreshAsync();
            AssertColor("#AB12CD", cards[100].BorderBrush);
            AssertColor(expected[500], cards[500].BorderBrush);
            settings.ColorScheme.Overrides.Clear();
            await view.Model.RefreshAsync();
            AssertColor(expected[100], cards[100].BorderBrush);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
            Application.Current.RequestedThemeVariant = originalTheme;
        }
    }

    private static void AssertColor(string expected, IBrush? brush)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }
}
