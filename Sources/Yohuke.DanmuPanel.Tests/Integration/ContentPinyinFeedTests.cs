using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证正文注音在真实消息列表中的绑定、显隐、配色与点击行为。</summary>
public sealed class ContentPinyinFeedTests
{
    /// <summary>普通与紧凑模式均响应开关及排版更新，并在仅译文时隐藏整块注音。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false, PinyinLayoutMode.Ruby)]
    [InlineData(MessageKind.Danmu, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(MessageKind.Danmu, true, PinyinLayoutMode.Ruby)]
    [InlineData(MessageKind.Danmu, true, PinyinLayoutMode.SeparateLine)]
    [InlineData(MessageKind.SuperChat, false, PinyinLayoutMode.Ruby)]
    [InlineData(MessageKind.SuperChat, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(MessageKind.SuperChat, true, PinyinLayoutMode.Ruby)]
    [InlineData(MessageKind.SuperChat, true, PinyinLayoutMode.SeparateLine)]
    public async Task ExistingRowsApplyPinyinSettingsAndTranslationVisibility(MessageKind kind, bool compact,
        PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = false;
        settings.CompactSuperChatWrap = false;
        settings.AutoScrollLongMessages = false;
        settings.PinyinLayout = layout;
        var record = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind,
            UserName = "viewer", Content = "你好ABC", TotalPrice = 30
        };
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(record, settings.Reading, fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(record.Id, record.Generation, fingerprint, "Hello ABC"));
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Width = 640, Height = 500, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var original = Original(view);
            var marquee = view.GetVisualDescendants().OfType<HoverMarqueeTextBlock>()
                .Single(control => control.Classes.Contains("originalText"));
            Assert.Empty(original.Annotations!);

            settings.ShowDanmuPinyin = kind == MessageKind.Danmu;
            settings.ShowSuperChatPinyin = kind == MessageKind.SuperChat;
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(original, Original(view));
            Assert.Same(item.ContentPinyin, original.Annotations);
            Assert.Same(item.ContentPinyin, marquee.Annotations);
            Assert.Equal(layout, original.PinyinLayout);
            Assert.Equal(layout, marquee.PinyinLayout);
            Assert.Equal(compact, marquee.IsEffectivelyVisible);
            Assert.Equal(!compact, original.IsEffectivelyVisible);
            var activeText = compact
                ? Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>())
                : original;
            Assert.NotEmpty(activeText.GetVisualDescendants().OfType<TextBlock>());

            settings.ColorScheme.Overrides["MessagePinyinBrush"] = "#C02070";
            runtime.Colors.Apply(Application.Current!, settings.ColorScheme);
            Pump(window);
            Assert.Equal(Color.Parse("#C02070"), Assert.IsAssignableFrom<ISolidColorBrush>(activeText.PinyinForeground).Color);
            var pronunciation = layout == PinyinLayoutMode.Ruby
                ? activeText.GetVisualDescendants().OfType<PinyinRubySyllable>().First().PronunciationText
                : Assert.Single(activeText.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()).PronunciationText;
            Assert.Equal(Color.Parse("#C02070"), Assert.IsAssignableFrom<ISolidColorBrush>(pronunciation.Foreground).Color);

            settings.PinyinLayout = layout == PinyinLayoutMode.Ruby
                ? PinyinLayoutMode.SeparateLine : PinyinLayoutMode.Ruby;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Equal(settings.PinyinLayout, activeText.PinyinLayout);

            settings.Translation.DisplayMode = TranslationDisplayMode.TranslationOnly;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.False(original.IsEffectivelyVisible);
            Assert.False(marquee.IsEffectivelyVisible);
            Assert.All(activeText.GetVisualDescendants().OfType<TextBlock>(), text => Assert.False(text.IsEffectivelyVisible));

            settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
            settings.ShowDanmuPinyin = false;
            settings.ShowSuperChatPinyin = false;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Empty(activeText.Annotations!);
            Assert.Empty(activeText.GetVisualDescendants().OfType<PinyinRubySyllable>());
            Assert.Empty(activeText.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
            Assert.Same(item, Assert.Single(view.Model.Items));
        }
        finally
        {
            runtime.Colors.Apply(Application.Current!, new());
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>点击注音正文中的汉字仍提交弹幕置顶或 SC 已读。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, PinyinLayoutMode.Ruby)]
    [InlineData(MessageKind.Danmu, PinyinLayoutMode.SeparateLine)]
    [InlineData(MessageKind.SuperChat, PinyinLayoutMode.Ruby)]
    [InlineData(MessageKind.SuperChat, PinyinLayoutMode.SeparateLine)]
    public async Task ClickingAnnotatedChineseKeepsTheRowAction(MessageKind kind, PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = false;
        settings.ShowDanmuPinyin = true;
        settings.ShowSuperChatPinyin = true;
        settings.PinyinLayout = layout;
        var record = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, UserName = "viewer", Content = "你好", TotalPrice = 30
        };
        await runtime.Messages.AppendAsync(record, settings.Reading);
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Width = 640, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var original = Original(view);
            var character = layout == PinyinLayoutMode.Ruby
                ? original.GetVisualDescendants().OfType<PinyinRubySyllable>().First().OriginalText
                : Assert.Single(original.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()).OriginalText;
            var point = character.TranslatePoint(new(3, character.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            var completed = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                Pump(window);
                var restored = (await runtime.Messages.GetByIdAsync(record.Id))!;
                completed = kind == MessageKind.Danmu ? restored.IsPinned : restored.IsRead;
                if (completed)
                {
                    break;
                }
                await Task.Delay(10);
            }
            Assert.True(completed, "A pointer click on annotated text should reach the message row.");
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>用户名和礼物名在两种排版和显示模式下均支持音节定位与独立开关。</summary>
    [AvaloniaTheory]
    [InlineData(false, PinyinLayoutMode.Ruby)]
    [InlineData(false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, PinyinLayoutMode.Ruby)]
    [InlineData(true, PinyinLayoutMode.SeparateLine)]
    public async Task UserAndGiftNamesHaveTheSameHoverAndLayoutBehavior(bool compact, PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.AutoScrollLongMessages = false;
        settings.ShowUserPinyin = true;
        settings.ShowGiftPinyin = true;
        settings.PinyinLayout = layout;
        var record = new MessageRecord
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Gift,
            UserName = "小明", GiftName = "鲜花", Quantity = 2, TotalPrice = 20
        };
        await runtime.Messages.AppendAsync(record, settings.Reading);
        var view = new MessageFeedView(runtime, MessageKind.Gift, exception => throw exception);
        var window = new Window { Width = 1000, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var name = view.GetVisualDescendants().OfType<EmoticonTextBlock>()
                .Single(control => control.Classes.Contains("danmuFeedUser"));
            var body = view.GetVisualDescendants().OfType<EmoticonTextBlock>()
                .Single(control => control.ContentText == Assert.Single(view.Model.Items).Detail && control.IsEffectivelyVisible);
            Assert.Equal(layout, name.PinyinLayout);
            Assert.Equal(layout, body.PinyinLayout);
            Assert.NotEmpty(name.Annotations!);
            Assert.NotEmpty(body.Annotations!);

            window.MouseMove(FirstCharacterPoint(name, window));
            Assert.Equal(0, name.HighlightedSourceStart);
            window.MouseMove(FirstCharacterPoint(body, window));
            Assert.Equal(-1, name.HighlightedSourceStart);
            Assert.Equal(0, body.HighlightedSourceStart);
            window.MouseMove(new(1, window.Bounds.Height - 1));
            Assert.Equal(-1, body.HighlightedSourceStart);

            settings.ShowUserPinyin = false;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Empty(name.Annotations!);
            Assert.NotEmpty(body.Annotations!);
            settings.ShowGiftPinyin = false;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Empty(body.Annotations!);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static Point FirstCharacterPoint(EmoticonTextBlock text, Window window)
    {
        var original = text.PinyinLayout == PinyinLayoutMode.Ruby
            ? text.GetVisualDescendants().OfType<PinyinRubySyllable>().First().OriginalText
            : Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()).OriginalText;
        var bounds = original.TextLayout.HitTestTextRange(0, 1).First();
        return original.TranslatePoint(bounds.Center, window)!.Value;
    }

    /// <summary>紧凑单行中仅一列包含拼音时，姓名和正文仍共享汉字正文基线。</summary>
    [AvaloniaTheory]
    [InlineData("中文", "mua", 1.0)]
    [InlineData("中文", "mua", 1.5)]
    [InlineData("Latin", "你好", 1.0)]
    [InlineData("Latin", "你好", 1.5)]
    [InlineData("中文", "nano好", 1.0)]
    [InlineData("中文", "nano好", 1.5)]
    [InlineData("中文", "打call", 1.0)]
    [InlineData("中文", "打call", 1.5)]
    public async Task CompactSingleLineRowsShareTheNameAndContentBaseline(string userName, string content, double scale)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = true;
        settings.CompactDanmuWrap = false;
        settings.AutoScrollLongMessages = false;
        settings.ShowUserPinyin = true;
        settings.ShowDanmuPinyin = true;
        settings.MessageTextScale = scale;
        await runtime.Messages.AppendAsync(new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = MessageKind.Danmu, UserName = userName, Content = content
        }, settings.Reading);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 1000, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var name = view.GetVisualDescendants().OfType<EmoticonTextBlock>()
                .Single(control => control.Classes.Contains("danmuFeedUser"));
            var body = view.GetVisualDescendants().OfType<EmoticonTextBlock>()
                .Single(control => control.ContentText == content && control.IsEffectivelyVisible);
            var nameBaseline = MainBaseline(name, window);
            var bodyBaseline = MainBaseline(body, window);
            Assert.InRange(Math.Abs(nameBaseline - bodyBaseline), 0, 1);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static double MainBaseline(EmoticonTextBlock text, Window window)
    {
        var original = text.GetVisualDescendants().OfType<PinyinRubySyllable>().FirstOrDefault()?.OriginalText ?? text;
        return original.TranslatePoint(new(0, original.TextLayout.Baseline), window)!.Value.Y;
    }

    private static EmoticonTextBlock Original(MessageFeedView view)
    {
        return view.GetVisualDescendants().OfType<EmoticonTextBlock>()
            .Single(control => control.Classes.Contains("originalText"));
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
