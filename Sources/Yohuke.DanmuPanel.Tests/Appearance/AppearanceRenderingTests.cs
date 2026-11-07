using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Appearance;

/// <summary>验证全局配色同步现有消息、浮动宿主及紧凑滚动文本。</summary>
public sealed class AppearanceRenderingTests
{
    /// <summary>切换配色无需重建窗口，普通原文和译文与滚动版本同步更新。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColorsUpdateAcrossExistingHostsAndTextModes(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = false;
        var record = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu,
            UserName = "观众", Content = "原文", Translation = "translated text",
            TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation)
        };
        await runtime.Messages.AppendAsync(record, new());
        var first = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var second = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 800, Height = 400, Content = first };
        var floating = new Window { Width = 600, Height = 400, Content = second };
        try
        {
            await first.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            await second.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            floating.Show();
            settings.ColorScheme.Overrides["MessageOriginalBrush"] = "#123456";
            settings.ColorScheme.Overrides["MessageTranslationBrush"] = "#80654321";
            runtime.Colors.Apply(Application.Current!, settings.ColorScheme);
            window.UpdateLayout();
            floating.UpdateLayout();
            foreach (var view in new[] { first, second })
            {
                var original = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("originalText"));
                var translation = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("translatedText"));
                var originalMarquee = view.GetVisualDescendants().OfType<HoverMarqueeTextBlock>().Single(text => text.Classes.Contains("originalText"));
                var translationMarquee = view.GetVisualDescendants().OfType<HoverMarqueeTextBlock>().Single(text => text.Classes.Contains("translatedText"));
                AssertColor("#123456", original.Foreground);
                AssertColor("#80654321", translation.Foreground);
                AssertColor("#123456", originalMarquee.Foreground);
                AssertColor("#80654321", translationMarquee.Foreground);
                Assert.Equal(1, translation.Opacity);
                Assert.Equal(1, translationMarquee.Opacity);
                Assert.Equal(compact, originalMarquee.IsVisible);
            }
        }
        finally
        {
            first.Model.Dispose();
            second.Model.Dispose();
            window.Close();
            floating.Close();
        }
    }

    /// <summary>SC 默认尊重消息配色，用户可覆盖并恢复；三级粉丝牌底色沿用全局舰队颜色。</summary>
    [Fact]
    public void BorderOverridesPreserveMessageColorsAndRestoreDefaults()
    {
        var record = new MessageRecord { Kind = MessageKind.SuperChat, BorderColor = "#123456", MedalGuardLevel = 2 };
        var settings = new PanelSettings();
        var item = new MessageItemViewModel(record, settings, new PinyinService());
        AssertColor("#123456", item.BorderBrush);
        settings.ColorScheme.Overrides["SuperChatBorderBrush"] = "#AABBCC";
        settings.ColorScheme.Overrides["GuardAdmiralBrush"] = "#102030";
        item.Update(record, settings);
        AssertColor("#AABBCC", item.BorderBrush);
        AssertColor("#102030", item.MedalBackgroundBrush);
        AssertColor("White", item.MedalForegroundBrush);
        Assert.Equal("#123456", item.Record.BorderColor);
        settings.ColorScheme.Overrides.Clear();
        item.Update(record, settings);
        AssertColor("#123456", item.BorderBrush);
        AssertColor("#A46EE8", item.MedalBackgroundBrush);
        AssertColor("Black", item.MedalForegroundBrush);
    }

    private static void AssertColor(string expected, IBrush? brush)
    {
        Assert.Equal(Color.Parse(expected), Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color);
    }
}
