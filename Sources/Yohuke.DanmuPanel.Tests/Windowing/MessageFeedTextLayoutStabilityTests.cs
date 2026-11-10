using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证消息行回收后的文字对齐，以及单行占位替换前后的布局稳定性。</summary>
public sealed class MessageFeedTextLayoutStabilityTests(ITestOutputHelper output)
{
    /// <summary>拼音超出紧凑视口而正文仍能同行时，等高等宽的内容更新也刷新译文位置。</summary>
    [AvaloniaFact]
    public async Task ClippedPinyinContentUpdateRepositionsInlineTranslation()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration(true, false, PinyinLayoutMode.SeparateLine, 1.5);
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var settings = MessageDisplaySettings.Capture(configuration.Settings);
        var record = new MessageRecord
        {
            Kind = MessageKind.Danmu, UserName = "姜茶J咖啡", Content = new string('窗', 12),
            Translation = "古守お姉さん",
            TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation)
        };
        var item = MessageItemViewModel.Create(record, settings, new PinyinService());
        var list = new MessageFeedListBox(() => new(MessageKind.Danmu))
        {
            ItemsSource = new ObservableCollection<MessageItemViewModel> { item }
        };
        var window = new Window { Width = 800, Height = 240, Content = Surface(model, list, 1.5) };
        try
        {
            window.Show();
            Pump(window);
            var row = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(item)).Row;
            var body = Assert.Single(row.GetVisualDescendants().OfType<MessageBodyPanel>(), value => value.IsEffectivelyVisible);
            var initialSize = body.Children[0].DesiredSize;
            foreach (var length in new[] { 10, 12, 11, 10 })
            {
                item.Apply(record with { Content = new string('窗', length) }, settings);
                Pump(window);
                var original = Assert.IsType<EmoticonTextBlock>(VisibleText(row, "originalText"));
                var presenter = Assert.Single(original.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
                var translation = VisibleText(row, "translatedText");
                var originalPosition = presenter.OriginalText.TranslatePoint(default, body)!.Value;
                var translationPosition = translation.TranslatePoint(default, body)!.Value;
                var gap = translationPosition.X - originalPosition.X - presenter.OriginalText.TextLayout.WidthIncludingTrailingWhitespace;
                output.WriteLine($"length={length}, region={initialSize}->{body.Children[0].DesiredSize}, original={presenter.OriginalText.DesiredSize}, translation={translationPosition}, gap={gap}");
                Assert.Equal(initialSize, body.Children[0].DesiredSize);
                Assert.InRange(gap, body.HorizontalSpacing - 1, body.HorizontalSpacing + 1);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>反复跨页滚动后，当前原文和译文仍按实际文字基线排列。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, PinyinLayoutMode.Ruby)]
    [InlineData(false, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, false, PinyinLayoutMode.Ruby)]
    [InlineData(true, false, PinyinLayoutMode.SeparateLine)]
    [InlineData(true, true, PinyinLayoutMode.Ruby)]
    [InlineData(true, true, PinyinLayoutMode.SeparateLine)]
    public async Task RapidScrollingKeepsCurrentTranslationAligned(bool compact, bool wrap, PinyinLayoutMode layout)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration(compact, wrap, layout, 1.5);
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var settings = MessageDisplaySettings.Capture(configuration.Settings);
        var pinyin = new PinyinService();
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation);
        var source = new ObservableCollection<MessageItemViewModel>(Enumerable.Range(0, 160).Select(index =>
            MessageItemViewModel.Create(new()
            {
                Id = "layout-" + index, Kind = MessageKind.Danmu, UserName = "姜茶J咖啡",
                Content = (index % 4) switch
                {
                    0 => "古守大姐姐",
                    1 => new string('窗', 8),
                    2 => "测试 abc",
                    _ => "你好"
                },
                Translation = "古守お姉さん", TranslationFingerprint = fingerprint
            }, settings, pinyin)));
        var list = new MessageFeedListBox(() => new(MessageKind.Danmu)) { ItemsSource = source };
        var surface = Surface(model, list, 1.5);
        var window = new Window { Width = 1600, Height = 360, Content = surface };
        var seenRows = new HashSet<MessageFeedRow>();
        var checkedMessages = new HashSet<string>();
        var reusedRows = 0;
        try
        {
            window.Show();
            for (var pass = 0; pass < 24; pass++)
            {
                list.ScrollIntoView(source[pass * 73 % source.Count]);
                Pump(window);
                foreach (var container in list.GetRealizedContainers().OfType<MessageFeedListBoxItem>())
                {
                    var row = container.Row;
                    var item = Assert.IsType<MessageItemViewModel>(container.DataContext);
                    Assert.Same(item, row.DataContext);
                    if (!seenRows.Add(row))
                    {
                        reusedRows++;
                    }
                    var original = Assert.IsType<EmoticonTextBlock>(VisibleText(row, "originalText"));
                    var translation = VisibleText(row, "translatedText");
                    Assert.Equal(item.Detail, original.ContentText);
                    Assert.Equal(item.Translation, translation.Text ?? (translation as EmoticonTextBlock)?.ContentText);
                    if (!original.IsSingleTextLine || !EmoticonTextBlock.HasSingleTextLine(translation))
                    {
                        continue;
                    }
                    var body = Assert.Single(row.GetVisualDescendants().OfType<MessageBodyPanel>(), value => value.IsEffectivelyVisible);
                    var glyph = layout == PinyinLayoutMode.SeparateLine
                        ? Assert.Single(original.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>()).OriginalText
                        : original.GetVisualDescendants().OfType<PinyinRubySyllable>().First().OriginalText;
                    var originalPosition = glyph.TranslatePoint(default, body)!.Value;
                    var translationPosition = translation.TranslatePoint(default, body)!.Value;
                    var baselineDelta = Math.Abs(originalPosition.Y + glyph.TextLayout.Baseline -
                                                 translationPosition.Y - translation.TextLayout.Baseline);
                    output.WriteLine($"pass={pass}, item={item.Record.Id}, original={glyph.Bounds}, translation={translation.Bounds}, baseline delta={baselineDelta}");
                    Assert.InRange(baselineDelta, 0, 1);
                    if (layout == PinyinLayoutMode.SeparateLine)
                    {
                        var gap = translationPosition.X - originalPosition.X - glyph.TextLayout.WidthIncludingTrailingWhitespace;
                        Assert.InRange(gap, body.HorizontalSpacing - 1, body.HorizontalSpacing + 1);
                    }
                    checkedMessages.Add(item.Record.Id);
                }
            }
            Assert.True(reusedRows > 0);
            Assert.True(checkedMessages.Count > 30);
            Assert.InRange(seenRows.Count, 1, source.Count - 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>没有换行时，拼音完成前后的姓名区和正文区高度保持一致。</summary>
    [AvaloniaTheory]
    [InlineData(false, PinyinLayoutMode.Ruby, 1.0)]
    [InlineData(false, PinyinLayoutMode.SeparateLine, 1.0)]
    [InlineData(true, PinyinLayoutMode.Ruby, 1.0)]
    [InlineData(true, PinyinLayoutMode.SeparateLine, 1.0)]
    [InlineData(false, PinyinLayoutMode.Ruby, 1.5)]
    [InlineData(false, PinyinLayoutMode.SeparateLine, 1.5)]
    [InlineData(true, PinyinLayoutMode.Ruby, 1.5)]
    [InlineData(true, PinyinLayoutMode.SeparateLine, 1.5)]
    public async Task SingleLinePinyinCompletionPreservesRegionHeights(bool compact, PinyinLayoutMode layout, double scale)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration(compact, false, layout, scale);
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var settings = MessageDisplaySettings.Capture(configuration.Settings);
        var pinyin = new PinyinService();
        var record = new MessageRecord { Kind = MessageKind.Danmu, UserName = "姜茶J咖啡", Content = "古守大姐姐" };
        var item = MessageItemViewModel.CreateDeferred(record, settings, pinyin, false);
        var list = new MessageFeedListBox(() => new(MessageKind.Danmu))
        {
            ItemsSource = new ObservableCollection<MessageItemViewModel> { item }
        };
        var window = new Window { Width = 1600, Height = 240, Content = Surface(model, list, scale) };
        try
        {
            window.Show();
            Pump(window);
            var row = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(item)).Row;
            var placeholders = row.GetVisualDescendants().OfType<PinyinLoadingPlaceholder>()
                .Where(value => !value.GetVisualAncestors().OfType<MessageEventSummarySurface>().Any()).ToArray();
            Assert.Equal(2, placeholders.Length);
            Assert.All(placeholders, placeholder => Assert.True(placeholder.IsEffectivelyVisible));
            var nameRegion = Assert.IsType<StackPanel>(placeholders.Single(control => control.Classes.Contains("namePinyinPending")).Parent);
            var contentRegion = Assert.IsType<StackPanel>(placeholders.Single(control => control.Classes.Contains("contentPinyinPending")).Parent);
            var pendingNameHeight = nameRegion.Bounds.Height;
            var pendingContentHeight = contentRegion.Bounds.Height;

            Assert.True(item.ApplyPresentation(new(record, settings.PresentationOptions,
                pinyin.GetPinyin(record.UserName), pinyin.GetAnnotations(record.UserName), "",
                pinyin.GetAnnotations(record.Content), true)));
            Pump(window);

            Assert.All(placeholders, placeholder => Assert.False(placeholder.IsVisible));
            Assert.True(Assert.IsType<EmoticonTextBlock>(VisibleText(row, "originalText")).IsSingleTextLine);
            output.WriteLine($"name={pendingNameHeight}->{nameRegion.Bounds.Height}, content={pendingContentHeight}->{contentRegion.Bounds.Height}");
            Assert.InRange(Math.Abs(nameRegion.Bounds.Height - pendingNameHeight), 0, 0.5);
            Assert.InRange(Math.Abs(contentRegion.Bounds.Height - pendingContentHeight), 0, 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>单行翻译替换等待文字时，译文区域及整个正文的高度保持一致。</summary>
    [AvaloniaTheory]
    [InlineData(false, 1.0, "古守お姉さん")]
    [InlineData(true, 1.0, "古守お姉さん")]
    [InlineData(false, 1.5, "古守お姉さん")]
    [InlineData(true, 1.5, "古守お姉さん")]
    [InlineData(false, 1.0, "Translation")]
    [InlineData(true, 1.0, "Translation")]
    [InlineData(false, 1.5, "Translation")]
    [InlineData(true, 1.5, "Translation")]
    [InlineData(false, 1.0, "Привет")]
    [InlineData(true, 1.0, "Привет")]
    [InlineData(false, 1.5, "Привет")]
    [InlineData(true, 1.5, "Привет")]
    [InlineData(false, 1.0, "مرحبا")]
    [InlineData(true, 1.0, "مرحبا")]
    [InlineData(false, 1.5, "مرحبا")]
    [InlineData(true, 1.5, "مرحبا")]
    public async Task SingleLineTranslationCompletionPreservesBodyHeight(bool compact, double scale, string translatedText)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = Configuration(compact, false, PinyinLayoutMode.SeparateLine, scale);
        configuration.Settings.ShowDanmuPinyin = false;
        configuration.Settings.ShowUserPinyin = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        var settings = MessageDisplaySettings.Capture(configuration.Settings);
        var record = new MessageRecord { Kind = MessageKind.Danmu, UserName = "viewer", Content = "古守大姐姐" };
        var item = MessageItemViewModel.Create(record, settings, new PinyinService(), true);
        var list = new MessageFeedListBox(() => new(MessageKind.Danmu))
        {
            ItemsSource = new ObservableCollection<MessageItemViewModel> { item }
        };
        var window = new Window { Width = 1600, Height = 240, Content = Surface(model, list, scale) };
        try
        {
            window.Show();
            Pump(window);
            var row = Assert.IsType<MessageFeedListBoxItem>(list.ContainerFromItem(item)).Row;
            var body = Assert.Single(row.GetVisualDescendants().OfType<MessageBodyPanel>(), value => value.IsEffectivelyVisible);
            var translationRegion = Assert.Single(body.GetVisualDescendants().OfType<Grid>(), control => control.Classes.Contains("translationLine"));
            var placeholder = Assert.Single(body.GetVisualDescendants().OfType<ShimmerTextBlock>());
            Assert.True(placeholder.IsEffectivelyVisible);
            var pendingBodyHeight = body.Bounds.Height;
            var pendingTranslationHeight = translationRegion.Bounds.Height;

            item.Apply(record with
            {
                Translation = translatedText,
                TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation)
            }, settings);
            Pump(window);

            Assert.False(placeholder.IsVisible);
            Assert.True(EmoticonTextBlock.HasSingleTextLine(VisibleText(row, "translatedText")));
            output.WriteLine($"body={pendingBodyHeight}->{body.Bounds.Height}, translation={pendingTranslationHeight}->{translationRegion.Bounds.Height}");
            Assert.InRange(Math.Abs(translationRegion.Bounds.Height - pendingTranslationHeight), 0, 0.5);
            Assert.InRange(Math.Abs(body.Bounds.Height - pendingBodyHeight), 0, 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    private static PanelConfiguration Configuration(bool compact, bool wrap, PinyinLayoutMode layout, double scale)
    {
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                CompactDanmuDisplay = compact, CompactDanmuWrap = wrap,
                AutoScrollLongMessages = false, ShowUserPinyin = true, ShowDanmuPinyin = true,
                PinyinLayout = layout, MessageTextScale = scale
            }
        };
        configuration.Settings.Translation.Enabled = true;
        configuration.Settings.Translation.DisplayMode = TranslationDisplayMode.Bilingual;
        return configuration;
    }

    private static MessageFeedSurface Surface(FeedViewModel model, MessageFeedListBox list, double scale)
    {
        var surface = new MessageFeedSurface { DataContext = model };
        surface.Host.Content = list;
        surface.Resources["MessageFontSizeBody"] = 13 * scale;
        surface.Resources["MessageFontSizeCompact"] = 12 * scale;
        surface.Resources["MessageFontSizePinyin"] = 10 * scale;
        surface.Resources["MessageFontSizeMetadata"] = 11 * scale;
        return surface;
    }

    private static TextBlock VisibleText(MessageFeedRow row, string className)
    {
        var text = row.GetVisualDescendants().OfType<TextBlock>()
            .SingleOrDefault(control => control.Classes.Contains(className) && control.IsEffectivelyVisible);
        if (text is not null)
        {
            return text;
        }
        var marquee = Assert.Single(row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>(),
            control => control.Classes.Contains(className) && control.IsEffectivelyVisible);
        return Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
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
