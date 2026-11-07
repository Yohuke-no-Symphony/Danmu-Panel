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
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证舰队粉丝牌的实色底色、文字对比度和主题回退。</summary>
public sealed class FanMedalColorRenderingTests
{
    /// <summary>两种主题和布局中，置顶及普通粉丝牌用底色区分舰队等级，并原位响应颜色和身份更新。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GuardMedalsUseFilledBackgroundsAndRefreshColors(bool compact, bool dark)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        for (var level = 0; level <= 3; level++)
        {
            var record = new MessageRecord
            {
                Id = $"medal-{level}", RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu,
                UserName = $"观众{level}", Content = "粉丝牌底色", MedalName = "粉丝牌", MedalLevel = 20,
                MedalGuardLevel = level
            };
            await runtime.Messages.AppendAsync(record, settings.Reading);
        }
        await runtime.Messages.AppendAsync(new()
        {
            Id = "without-medal", RoomTabId = "room", RoomId = "123", Kind = MessageKind.Danmu,
            UserName = "未戴牌观众", Content = "无粉丝牌", MedalGuardLevel = 3
        }, settings.Reading);
        await runtime.Messages.TogglePinAsync("medal-2");
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window
        {
            Width = 760, Height = 480, Content = view,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "room", RoomId = "123" });
            window.Show();
            Pump(window);
            var badges = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("danmuMedalBadge"))
                .ToDictionary(border => Assert.IsType<MessageItemViewModel>(border.DataContext).Record.Id);
            Assert.Equal(5, badges.Count);
            Assert.Single(view.Model.PinnedItems);
            var ordinaryBackground = Assert.IsAssignableFrom<ISolidColorBrush>(badges["medal-0"].Background);
            var ordinaryForeground = Assert.IsAssignableFrom<ISolidColorBrush>(MedalTexts(badges["medal-0"])[0].Foreground);
            var placeholder = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("danmuMedalPlaceholder") && border.IsEffectivelyVisible);
            var placeholderText = Assert.IsType<TextBlock>(placeholder.Child);
            var placeholderForeground = placeholderText.Foreground;
            AssertReadable(badges["medal-0"]);
            AssertReadable(placeholder);
            Assert.Equal(new Thickness(1), placeholder.BorderThickness);
            var expected = new Dictionary<int, string>
            {
                [1] = "#E9A23B", [2] = "#A46EE8", [3] = "#4F8FDD"
            };
            foreach (var (level, color) in expected)
            {
                AssertBadge(badges[$"medal-{level}"], color, "Black");
            }
            CaptureIfRequested(window, $"fan-medals-{(dark ? "dark" : "light")}-{(compact ? "compact" : "normal")}.png");

            settings.ColorScheme.Overrides["GuardGovernorBrush"] = "#224466";
            settings.ColorScheme.Overrides["GuardAdmiralBrush"] = "#335577";
            settings.ColorScheme.Overrides["GuardCaptainBrush"] = "#446688";
            runtime.Colors.Apply(Application.Current!, settings.ColorScheme);
            await view.Model.RefreshAsync();
            Pump(window);
            foreach (var (level, color) in expected)
            {
                AssertBadge(badges[$"medal-{level}"], color, "Black");
            }

            settings.ColorScheme.Overrides["MedalAdmiralBackgroundBrush"] = "#102030";
            settings.ColorScheme.Overrides["MedalCaptainBackgroundBrush"] = "#FEFAE0";
            settings.ColorScheme.Overrides["MedalBackgroundBrush"] = "#FEDCBA";
            settings.ColorScheme.Overrides["MedalTextBrush"] = "#345678";
            runtime.Colors.Apply(Application.Current!, settings.ColorScheme);
            await view.Model.RefreshAsync();
            Pump(window);
            AssertBadge(badges["medal-1"], expected[1], "Black");
            AssertBadge(badges["medal-2"], "#102030", "White");
            AssertBadge(badges["medal-3"], "#FEFAE0", "#345678");
            AssertBadge(badges["medal-0"], "#FEDCBA", "#345678");
            Assert.Same(placeholderForeground, placeholderText.Foreground);

            var ordinary = Assert.IsType<MessageItemViewModel>(badges["medal-0"].DataContext);
            var ordinaryRecord = ordinary.Record;
            ordinary.Update(ordinaryRecord with { MedalGuardLevel = 2 }, settings);
            Pump(window);
            AssertBadge(badges["medal-0"], "#102030", "White");
            ordinary.Update(ordinaryRecord, settings);
            Pump(window);
            AssertBadge(badges["medal-0"], "#FEDCBA", "#345678");

            foreach (var key in new[] { "MedalBackgroundBrush", "MedalCaptainBackgroundBrush", "MedalAdmiralBackgroundBrush",
                         "MedalGovernorBackgroundBrush", "MedalTextBrush" })
            {
                settings.ColorScheme.Overrides[key] = "#FFFFFF";
            }
            runtime.Colors.Apply(Application.Current!, settings.ColorScheme);
            await view.Model.RefreshAsync();
            Pump(window);
            foreach (var level in Enumerable.Range(0, 4))
            {
                AssertBadge(badges[$"medal-{level}"], "White", "Black");
            }
            CaptureIfRequested(window, $"fan-medals-white-{(dark ? "dark" : "light")}-{(compact ? "compact" : "normal")}.png");

            settings.ColorScheme.Overrides.Clear();
            runtime.Colors.Apply(Application.Current!, settings.ColorScheme);
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal(ordinaryBackground.Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(badges["medal-0"].Background).Color);
            Assert.Equal(ordinaryBackground.Opacity, badges["medal-0"].Background!.Opacity);
            Assert.All(MedalTexts(badges["medal-0"]), text => AssertColor(ordinaryForeground.Color, text.Foreground));
            foreach (var (level, color) in expected)
            {
                AssertBadge(badges[$"medal-{level}"], color, "Black");
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static TextBlock[] MedalTexts(Border badge)
    {
        return badge.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("danmuMedalText")).ToArray();
    }

    private static void AssertBadge(Border badge, string background, string foreground)
    {
        AssertColor(Color.Parse(background), badge.Background);
        Assert.Equal(new Thickness(0), badge.BorderThickness);
        var texts = MedalTexts(badge);
        Assert.Equal(2, texts.Length);
        Assert.All(texts, text => AssertColor(Color.Parse(foreground), text.Foreground));
        AssertReadable(badge);
    }

    private static void AssertReadable(Border badge)
    {
        var adaptive = Assert.IsType<AdaptiveForegroundBorder>(badge);
        foreach (var text in MedalTexts(badge))
        {
            var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground);
            var color = ColorContrast.Composite(foreground, adaptive.EffectiveBackground);
            Assert.True(ColorContrast.GetContrastRatio(color,
                adaptive.EffectiveBackground) >= ColorContrast.MINIMUM_TEXT_CONTRAST);
        }
    }

    private static void AssertColor(Color expected, IBrush? brush)
    {
        Assert.Equal(expected, Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void CaptureIfRequested(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_FAN_MEDAL_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name));
    }
}
