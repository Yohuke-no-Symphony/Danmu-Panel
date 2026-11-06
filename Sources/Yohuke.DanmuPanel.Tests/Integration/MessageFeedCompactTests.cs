using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实列表布局验证四个面板的紧凑排版和翻译显示。</summary>
public sealed class MessageFeedCompactTests
{
    /// <summary>四类短消息缩小行高、隐藏时间，并保持正文对齐和已有行对象。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task CompactRowsAreShorterAndKeepTheirMessageViewModels(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = false;
        settings.CompactDanmuWrap = false;
        settings.CompactSuperChatWrap = false;
        var message = CreateMessage(kind);
        await runtime.Messages.AppendAsync(message, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 640, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var normalHeight = row.Bounds.Height;
            Assert.True(normalHeight > 0);
            Assert.True(FindTime(row).IsEffectivelyVisible);
            AssertBodyAlignment(row);

            settings.CompactDanmuDisplay = true;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Same(item, Assert.Single(view.Model.Items));
            row = FindRow(view, message.Id);
            Assert.True(row.Bounds.Height < normalHeight,
                $"Expected {kind} compact row below {normalHeight}px, got {row.Bounds.Height}px.");
            Assert.False(FindTime(row).IsEffectivelyVisible);
            AssertBodyAlignment(row);
            var compactText = Assert.IsType<HoverMarqueeTextBlock>(Assert.Single(VisibleBodyContents(row)));
            Assert.Equal(12, Assert.Single(compactText.GetVisualDescendants().OfType<EmoticonTextBlock>()).FontSize);

            settings.CompactDanmuDisplay = false;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Same(item, Assert.Single(view.Model.Items));
            row = FindRow(view, message.Id);
            Assert.True(FindTime(row).IsEffectivelyVisible);
            Assert.InRange(Math.Abs(row.Bounds.Height - normalHeight), 0, 0.5);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>普通消息和 SC 的紧凑换行独立生效，关闭紧凑后均恢复多行。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task CompactWrapOptionsRemainIndependentAndNormalModeAlwaysWraps(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = false;
        settings.CompactSuperChatWrap = false;
        var message = CreateMessage(kind, longText: true);
        await runtime.Messages.AppendAsync(message, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = kind == MessageKind.Guard ? 180 : 360, Height = 800, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            AssertWrapping(FindRow(view, message.Id), false);

            settings.CompactDanmuWrap = true;
            await view.Model.RefreshAsync();
            Pump(window);
            AssertWrapping(FindRow(view, message.Id), kind != MessageKind.SuperChat);
            Assert.Same(item, Assert.Single(view.Model.Items));

            settings.CompactDanmuWrap = false;
            settings.CompactSuperChatWrap = true;
            await view.Model.RefreshAsync();
            Pump(window);
            AssertWrapping(FindRow(view, message.Id), kind == MessageKind.SuperChat);
            Assert.Same(item, Assert.Single(view.Model.Items));

            settings.CompactDanmuDisplay = false;
            settings.CompactDanmuWrap = false;
            settings.CompactSuperChatWrap = false;
            await view.Model.RefreshAsync();
            Pump(window);
            AssertWrapping(FindRow(view, message.Id), true);
            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>紧凑和普通模式中的双语、仅译文及无译文回退均使用同一行对象。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    public async Task TranslationModesPreserveVisibleTextAndExistingRows(MessageKind kind, bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = false;
        settings.CompactSuperChatWrap = false;
        settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
        var translated = CreateMessage(kind) with { Content = "原文内容", Time = DateTimeOffset.UtcNow };
        var fallback = CreateMessage(kind) with { Content = "尚未翻译的原文", Time = translated.Time.AddSeconds(-1) };
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(translated, settings.Reading, fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(translated.Id, translated.Generation, fingerprint, "Translated content"));
        await runtime.Messages.AppendAsync(fallback, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 640, Height = 500, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var items = view.Model.Items.ToArray();
            Assert.Equal(new[] { translated.Content, "Translated content" }, VisibleBodyContents(FindRow(view, translated.Id)).Select(ReadContent));
            Assert.Equal(fallback.Content, ReadContent(Assert.Single(VisibleBodyContents(FindRow(view, fallback.Id)))));

            settings.Translation.DisplayMode = TranslationDisplayMode.TranslationOnly;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal("Translated content", ReadContent(Assert.Single(VisibleBodyContents(FindRow(view, translated.Id)))));
            Assert.Equal(fallback.Content, ReadContent(Assert.Single(VisibleBodyContents(FindRow(view, fallback.Id)))));
            Assert.All(items, item => Assert.Same(item, view.Model.Items.Single(current => current.Record.Id == item.Record.Id)));

            settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal(new[] { translated.Content, "Translated content" }, VisibleBodyContents(FindRow(view, translated.Id)).Select(ReadContent));
            Assert.All(items, item => Assert.Same(item, view.Model.Items.Single(current => current.Record.Id == item.Record.Id)));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>自动滚动同时作用于原文和译文，显示模式、换行与设置更新会正确启停。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task AutoScrollIncludesTranslationAndRespectsWrapping(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = false;
        settings.CompactSuperChatWrap = false;
        settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
        var message = CreateMessage(kind, longText: true);
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint,
            string.Concat(Enumerable.Repeat("Long translated content. ", 12))));
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Width = 500, Height = 700, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            window.MouseMove(new(1, 1));
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var marquees = VisibleBodyContents(FindRow(view, message.Id)).Cast<HoverMarqueeTextBlock>().ToArray();
            Assert.Equal(2, marquees.Length);
            var viewers = marquees.Select(marquee => Assert.Single(marquee.GetVisualDescendants().OfType<ScrollViewer>())).ToArray();
            Assert.All(marquees, marquee => Assert.True(marquee.AutoScroll));

            await WaitUntilAsync(() => viewers.All(viewer => viewer.Offset.X > 0), window);
            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.All(marquees, marquee => Assert.True(marquee.AutoScroll));

            settings.Translation.DisplayMode = TranslationDisplayMode.TranslationOnly;
            await view.Model.RefreshAsync();
            await WaitUntilAsync(() => viewers[0].Offset.X == 0, window);
            Assert.False(marquees[0].IsEffectivelyVisible);
            Assert.True(viewers[1].Offset.X > 0);

            settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
            settings.CompactDanmuWrap = true;
            settings.CompactSuperChatWrap = true;
            await view.Model.RefreshAsync();
            await WaitUntilAsync(() => viewers.All(viewer => viewer.Offset.X == 0), window);
            Assert.All(marquees, marquee => Assert.False(marquee.IsEffectivelyVisible));
            Assert.All(VisibleBodyContents(FindRow(view, message.Id)), content => Assert.IsAssignableFrom<TextBlock>(content));

            settings.CompactDanmuWrap = false;
            settings.CompactSuperChatWrap = false;
            await view.Model.RefreshAsync();
            await WaitUntilAsync(() => viewers.All(viewer => viewer.Offset.X > 0), window);

            settings.AutoScrollLongMessages = false;
            await view.Model.RefreshAsync();
            Pump(window);
            await Task.Delay(450);
            Pump(window);
            Assert.All(viewers, viewer => Assert.Equal(0, viewer.Offset.X));
            Assert.Same(item, Assert.Single(view.Model.Items));
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>明暗主题的置顶区与普通区分别从首行重新计算交替底色。</summary>
    [AvaloniaFact]
    public async Task DanmuAlternatingBackgroundFollowsThemeAndCurrentOrderAfterPinning()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var window = new Window { Width = 640, Height = 500, Content = view, RequestedThemeVariant = ThemeVariant.Light };
        try
        {
            for (var index = 0; index < 3; index++)
            {
                await runtime.Messages.AppendAsync(CreateMessage(MessageKind.Danmu) with
                {
                    Content = "row " + index, Time = DateTimeOffset.UtcNow.AddSeconds(index)
                }, runtime.Configuration.Settings.Reading);
            }
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var initial = view.Model.Items.ToArray();
            var light = AssertAlternating(view, view.Model.ScrollingItems);
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Pump(window);
            var dark = AssertAlternating(view, view.Model.ScrollingItems);
            Assert.NotEqual(light.Normal, dark.Normal);
            Assert.NotEqual(light.Alternate, dark.Alternate);

            await runtime.Messages.TogglePinAsync(initial[2].Record.Id);
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Same(initial[2], view.Model.Items[0]);
            Assert.Same(initial[0], view.Model.Items[1]);
            Assert.Same(initial[2], Assert.Single(view.Model.PinnedItems));
            Assert.Equal(2, view.Model.ScrollingItems.Count);
            var reordered = AssertAlternating(view, view.Model.ScrollingItems);
            Assert.Equal(dark, reordered);
            Assert.Equal(dark.Normal, BackgroundColor(FindRow(view, initial[2].Record.Id)));
            Assert.Equal(dark.Normal, BackgroundColor(FindRow(view, initial[0].Record.Id)));
            Assert.Equal(dark.Alternate, BackgroundColor(FindRow(view, initial[1].Record.Id)));
            await runtime.Messages.TogglePinAsync(initial[2].Record.Id);
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Empty(view.Model.PinnedItems);
            Assert.Equal(initial, view.Model.ScrollingItems);
            Assert.Equal(dark, AssertAlternating(view, view.Model.ScrollingItems));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new ConfigurationService(directory.Path), new PanelConfiguration());
    }

    private static MessageRecord CreateMessage(MessageKind kind, bool longText = false)
    {
        var content = longText ? string.Concat(Enumerable.Repeat("这是需要完整显示并自动换行的正文。", 8)) : "正文";
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, UserName = "A", Content = content,
            GiftName = longText ? content : "鲜花", Quantity = kind == MessageKind.Guard && longText ? int.MaxValue : 1,
            GuardLevel = 3, TotalPrice = 30, UnitPrice = 30
        };
    }

    private static Button FindRow(MessageFeedView view, string id)
    {
        return view.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static TextBlock FindTime(Button row)
    {
        return row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("danmuFeedMeta"));
    }

    private static Control[] VisibleBodyContents(Button row)
    {
        return row.GetVisualDescendants().OfType<Control>().Where(control =>
            control.IsEffectivelyVisible &&
            (control.Classes.Contains("regularFeedContent") || control.Classes.Contains("compactFeedContent"))).ToArray();
    }

    private static string? ReadContent(Control control)
    {
        return control switch
        {
            HoverMarqueeTextBlock marquee => marquee.Text,
            EmoticonTextBlock text => text.ContentText,
            TextBlock text => text.Text,
            _ => throw new InvalidOperationException("Unexpected message body control.")
        };
    }

    private static void AssertBodyAlignment(Button row)
    {
        var body = row.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Classes.Contains("messageBody"));
        Assert.Equal(VerticalAlignment.Center, body.VerticalAlignment);
        foreach (var content in VisibleBodyContents(row))
        {
            var text = content is HoverMarqueeTextBlock marquee
                ? Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>())
                : Assert.IsAssignableFrom<TextBlock>(content);
            Assert.Equal(TextAlignment.Left, text.TextAlignment);
        }
    }

    private static void AssertWrapping(Button row, bool wraps)
    {
        var content = Assert.Single(VisibleBodyContents(row));
        if (wraps)
        {
            var text = Assert.IsType<EmoticonTextBlock>(content);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            Assert.True(text.TextLayout.TextLines.Count > 1, "Expected long message text to occupy multiple lines.");
            Assert.True(text.Bounds.Height >= text.TextLayout.Height - 0.5);
        }
        else
        {
            var marquee = Assert.IsType<HoverMarqueeTextBlock>(content);
            var text = Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            Assert.Single(text.TextLayout.TextLines);
        }
        AssertBodyAlignment(row);
    }

    private static (Color Normal, Color Alternate) AssertAlternating(MessageFeedView view,
        IReadOnlyList<MessageItemViewModel> items)
    {
        var colors = items.Select(item => BackgroundColor(FindRow(view, item.Record.Id))).ToArray();
        Assert.True(colors.Length >= 2);
        Assert.NotEqual(colors[0], colors[1]);
        for (var index = 0; index < colors.Length; index++)
        {
            Assert.Equal(colors[index % 2], colors[index]);
        }
        return (colors[0], colors[1]);
    }

    private static Color BackgroundColor(Button row)
    {
        return Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
            Pump(window);
        }
        Assert.True(condition());
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
