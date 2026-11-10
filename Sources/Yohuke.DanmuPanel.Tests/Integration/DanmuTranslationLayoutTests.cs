using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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

/// <summary>验证弹幕原文与译文根据实际正文宽度同行，并对等待占位使用相同排版。</summary>
public sealed class DanmuTranslationLayoutTests
{
    /// <summary>短译文与等待占位均可同行，窗口、分栏和字号变化后原位重新判断。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ShortContentReflowsWhenAvailableWidthChanges(bool compact, bool wrap, bool pending)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact, wrap);
        var settings = runtime.Configuration.Settings;
        var message = Message("短弹幕");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        if (!pending)
        {
            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, "Short translation"));
        }
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 640, Height = 300, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var body = FindBody(row);
            AssertInline(body);
            SavePreview(window, $"translation-{compact}-{wrap}-{pending}-wide");

            window.Width = 220;
            Pump(window);
            AssertStacked(body);
            SavePreview(window, $"translation-{compact}-{wrap}-{pending}-narrow");

            window.Width = 640;
            Pump(window);
            AssertInline(body);

            settings.MessageColumns.Danmu.CompactIdentityRatio = 0.9;
            settings.MessageColumns.Danmu.RegularIdentityRatio = 0.9;
            runtime.NotifyAppearanceChanged();
            Pump(window);
            AssertStacked(body);

            settings.MessageColumns.Danmu.CompactIdentityRatio = 0.4;
            settings.MessageColumns.Danmu.RegularIdentityRatio = 0.4;
            runtime.NotifyAppearanceChanged();
            Pump(window);
            AssertInline(body);

            if (!pending)
            {
                window.Width = 440;
                Pump(window);
                AssertInline(body);
                settings.MessageTextScale = 2;
                await view.Model.RefreshAsync();
                await view.Model.WaitForPresentationAsync();
                Pump(window);
                AssertStacked(body);
                settings.MessageTextScale = 1;
                await view.Model.RefreshAsync();
                await view.Model.WaitForPresentationAsync();
                Pump(window);
                AssertInline(body);
            }

            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(row, FindRow(view, message.Id));
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>短占位在长译文返回后重新排版，仅译文模式不遗留原文空位或间距。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, TranslationDisplayMode.Bilingual)]
    [InlineData(true, false, TranslationDisplayMode.Bilingual)]
    [InlineData(true, true, TranslationDisplayMode.Bilingual)]
    [InlineData(false, false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(true, false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(true, true, TranslationDisplayMode.TranslationOnly)]
    public async Task CompletionReflowsExistingRowAndTranslationOnlyUsesFullWidth(bool compact, bool wrap,
        TranslationDisplayMode mode)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact, wrap);
        var settings = runtime.Configuration.Settings;
        settings.Translation.DisplayMode = mode;
        var message = Message("你好");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 640, Height = 500, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var body = FindBody(row);
            var placeholder = Assert.Single(body.GetVisualDescendants().OfType<ShimmerTextBlock>());
            Assert.True(placeholder.IsEffectivelyVisible);
            AssertInline(body);

            var translated = string.Concat(Enumerable.Repeat("This is a long translation. ", 15));
            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, translated));
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);

            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(row, FindRow(view, message.Id));
            Assert.Same(placeholder, Assert.Single(body.GetVisualDescendants().OfType<ShimmerTextBlock>()));
            Assert.False(placeholder.IsEffectivelyVisible);
            Assert.False(placeholder.IsShimmering);
            if (mode == TranslationDisplayMode.Bilingual)
            {
                AssertStacked(body);
            }
            else
            {
                Assert.False(body.Children[0].IsVisible);
                Assert.Equal(0, body.Children[1].Bounds.X);
                Assert.Equal(0, body.Children[1].Bounds.Y);
                Assert.InRange(Math.Abs(body.Bounds.Height - body.Children[1].Bounds.Height), 0, 0.5);
                Assert.InRange(Math.Abs(body.Bounds.Width - body.Children[1].Bounds.Width), 0, 0.5);
                settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
                await view.Model.RefreshAsync();
                await view.Model.WaitForPresentationAsync();
                Pump(window);
                AssertStacked(body);
            }
            SavePreview(window, $"translation-{compact}-{wrap}-long");
            var content = Assert.Single(row.GetVisualDescendants().OfType<Control>(), control =>
                control.Classes.Contains("translatedText") && control.IsEffectivelyVisible);
            if (compact && !wrap)
            {
                Assert.IsType<HoverMarqueeTextBlock>(content);
            }
            else
            {
                Assert.True(Assert.IsAssignableFrom<TextBlock>(content).TextLayout.TextLines.Count > 1);
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>原文或译文中明确包含换行时，即使自然宽度较短也保持上下排列。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task ExplicitLineBreaksKeepTranslationOnSeparateLine(bool compact, bool wrap, bool originalBreaks)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact, wrap);
        var settings = runtime.Configuration.Settings;
        var message = Message(originalBreaks ? "第一行\n第二行" : "短弹幕");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 640, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var body = FindBody(FindRow(view, message.Id));
            if (originalBreaks)
            {
                AssertStacked(body);
            }
            else
            {
                AssertInline(body);
            }

            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint,
                originalBreaks ? "Hello" : "First\r\nSecond"));
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertStacked(body);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>拼音、占位、译文、字号与显示模式切换后，同行文字始终共享主文字基线。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, PinyinLayoutMode.Ruby)]
    [InlineData(true, false, PinyinLayoutMode.Ruby)]
    [InlineData(true, true, PinyinLayoutMode.Ruby)]
    [InlineData(false, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, true, PinyinLayoutMode.SeparateLine)]
    public async Task InlineTranslationAlignsMainTextAcrossPinyinChanges(bool compact, bool wrap, PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, compact, wrap);
        var settings = runtime.Configuration.Settings;
        settings.ShowDanmuPinyin = true;
        settings.PinyinLayout = layout;
        var message = Message("晚安");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 640, Height = 130, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var row = FindRow(view, message.Id);
            var item = Assert.Single(view.Model.Items);
            var body = FindBody(row);
            Assert.NotEmpty(item.ContentPinyin);
            Assert.True(item.IsTranslating);
            AssertInline(body);
            SavePreview(window, $"baseline-{compact}-{wrap}-{layout}-pending");

            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, "おやすみ"));
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertInline(body);
            SavePreview(window, $"baseline-{compact}-{wrap}-{layout}-translated");

            settings.MessageTextScale = 1.5;
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertInline(body);

            settings.ShowDanmuPinyin = false;
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertInline(body);

            settings.Translation.DisplayMode = TranslationDisplayMode.TranslationOnly;
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            Assert.False(body.Children[0].IsVisible);
            Assert.Equal(0, body.Children[1].Bounds.Y);

            settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
            settings.ShowDanmuPinyin = true;
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            AssertInline(body);
            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(row, FindRow(view, message.Id));
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>拼音等待矩形占据上方空间，译文始终对齐原文而非整个等待区域。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public async Task PendingPinyinKeepsTranslationAlignedWithMainText(PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        var pinyin = new BlockingPinyinService();
        await using var runtime = CreateRuntime(directory, true, false, pinyin);
        var settings = runtime.Configuration.Settings;
        settings.ShowDanmuPinyin = true;
        settings.PinyinLayout = layout;
        var message = Message("你好");
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, "Hello"));
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 640, Height = 300, Content = view };
        try
        {
            window.Show();
            await view.Model.SelectRoomAsync(new() { Id = "tab" }).WaitAsync(TimeSpan.FromSeconds(5));
            await pinyin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var body = FindBody(row);
            var pending = Assert.Single(body.GetVisualDescendants().OfType<PinyinLoadingPlaceholder>());
            Assert.True(pending.IsEffectivelyVisible);
            AssertInline(body);

            pinyin.Release.TrySetResult();
            await view.Model.WaitForPresentationAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Pump(window);
            Assert.False(pending.IsEffectivelyVisible);
            AssertInline(body);
            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(row, FindRow(view, message.Id));
        }
        finally
        {
            pinyin.Release.TrySetResult();
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>底部跟随期间长译文让行高增加，列表仍停留在最新消息。</summary>
    [AvaloniaFact]
    public async Task LongTranslationCompletionPreservesFollowingLatest()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory, true, true);
        var settings = runtime.Configuration.Settings;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var messages = Enumerable.Range(0, 40)
            .Select(index => Message($"短弹幕{index}") with { Time = time.AddSeconds(index) }).ToArray();
        foreach (var message in messages)
        {
            await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        }
        var view = new MessageFeedView(runtime, MessageKind.Danmu, exception => throw exception);
        var window = new Window { Width = 500, Height = 260, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var list = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(), control => control.Name == "ScrollingMessages");
            var viewer = Assert.Single(list.GetVisualDescendants().OfType<ScrollViewer>(), control => control.TemplatedParent == list);
            Assert.True(viewer.ScrollBarMaximum.Y > 0);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
            var row = FindRow(view, messages[^1].Id);
            AssertInline(FindBody(row));
            var initialExtent = viewer.Extent.Height;
            var last = messages[^1];
            var item = view.Model.Items.Single(value => value.Record.Id == last.Id);

            Assert.True(await runtime.Messages.ApplyTranslationAsync(last.Id, last.Generation, fingerprint,
                string.Concat(Enumerable.Repeat("Long translation expands this row. ", 20))));
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);

            Assert.Same(item, view.Model.Items.Single(value => value.Record.Id == last.Id));
            AssertStacked(FindBody(FindRow(view, last.Id)));
            Assert.True(viewer.Extent.Height > initialExtent);
            Assert.False(view.Model.IsReadingHistory);
            Assert.InRange(Math.Abs(viewer.ScrollBarMaximum.Y - viewer.Offset.Y), 0, 1);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory, bool compact, bool wrap,
        IPinyinService? pinyin = null)
    {
        var runtime = new PanelRuntime(new(directory.Path), new(), pinyin);
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = wrap;
        settings.AutoScrollLongMessages = false;
        settings.Translation.Enabled = true;
        return runtime;
    }

    private static MessageRecord Message(string content)
    {
        return new() { RoomTabId = "tab", Kind = MessageKind.Danmu, UserName = "viewer", Content = content };
    }

    private static MessageFeedRow FindRow(MessageFeedView view, string id)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>(), row =>
            row.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static Panel FindBody(MessageFeedRow row)
    {
        return Assert.Single(row.GetVisualDescendants().OfType<Panel>(), panel => panel.Classes.Contains("messageBody") && panel.IsEffectivelyVisible);
    }

    private static void AssertInline(Panel body)
    {
        var original = body.Children[0];
        var translation = body.Children[1];
        Assert.True(original.IsVisible && translation.IsVisible);
        var text = original.GetVisualDescendants().OfType<EmoticonTextBlock>()
            .Single(control => control.IsEffectivelyVisible);
        var mainText = text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()
            .SingleOrDefault()?.OriginalText ?? text;
        var textRight = mainText.TranslatePoint(default, body)!.Value.X + mainText.Padding.Left +
                        mainText.TextLayout.WidthIncludingTrailingWhitespace;
        var spacing = Assert.IsType<MessageBodyPanel>(body).HorizontalSpacing;
        Assert.InRange(translation.Bounds.X - textRight, spacing - 1, spacing + 1);
        Assert.InRange(Math.Abs(MainTextBaseline(original, body) - MainTextBaseline(translation, body)), 0, 1);
        Assert.InRange(Math.Abs(body.Bounds.Height - Math.Max(original.Bounds.Bottom, translation.Bounds.Bottom)), 0, 0.5);
        Assert.InRange(translation.Bounds.Right, 0, body.Bounds.Width + 0.5);
    }

    private static double MainTextBaseline(Control region, Control relativeTo)
    {
        var pending = region.GetVisualDescendants().OfType<ShimmerTextBlock>()
            .SingleOrDefault(control => control.IsEffectivelyVisible);
        if (pending is not null)
        {
            return pending.TranslatePoint(default, relativeTo)!.Value.Y + pending.TextBaseline;
        }

        var text = region.GetVisualDescendants().OfType<TextBlock>().Single(control =>
            control.IsEffectivelyVisible && (control is EmoticonTextBlock || control.Classes.Contains("translatedText")));
        var ruby = text.GetVisualDescendants().OfType<PinyinRubySyllable>().FirstOrDefault();
        var separateLine = text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>().FirstOrDefault();
        var mainText = ruby?.OriginalText ?? separateLine?.OriginalText ?? text;
        return mainText.TranslatePoint(default, relativeTo)!.Value.Y + mainText.Padding.Top + mainText.TextLayout.Baseline;
    }

    private static void AssertStacked(Panel body)
    {
        var original = body.Children[0];
        var translation = body.Children[1];
        Assert.True(original.IsVisible && translation.IsVisible);
        Assert.Equal(0, original.Bounds.X);
        Assert.Equal(0, translation.Bounds.X);
        Assert.True(translation.Bounds.Y > original.Bounds.Bottom,
            $"Expected stacked content: original={original.Bounds}, translation={translation.Bounds}");
        Assert.InRange(Math.Abs(body.Bounds.Height - translation.Bounds.Bottom), 0, 0.5);
    }

    private static void SavePreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_TRANSLATION_PREVIEW_DIRECTORY") is { Length: > 0 } path)
        {
            Directory.CreateDirectory(path);
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(path, name + ".png"));
        }
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        }
    }
}
