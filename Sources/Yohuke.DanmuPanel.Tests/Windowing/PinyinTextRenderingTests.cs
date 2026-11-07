using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证正文注音在真实鼠标、换行与表情混排中的定位。</summary>
public sealed class PinyinTextRenderingTests
{
    /// <summary>汉字右半侧仍定位本字音节，离开正文后清除高亮。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void HoverHighlightsTheHanziUnderThePointerAndClearsOnExit(PinyinLayoutMode layout)
    {
        var text = CreateText("你好吗", layout);
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Pump(window);
            Assert.Equal(1, text.HighlightedSourceStart);
            AssertHighlightedPronunciation(text, "hǎo");

            window.MouseMove(new(290, 210));
            Pump(window);
            Assert.Equal(-1, text.HighlightedSourceStart);
            AssertNoHighlightedPronunciations(text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>逐字注音随汉字一起换行，拼音始终位于本字正上方。</summary>
    [AvaloniaFact]
    public void RubySyllablesWrapAsIndivisiblePairsAndFollowTypography()
    {
        var text = CreateText(new string('长', 20), PinyinLayoutMode.Ruby);
        text.Width = 130;
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            Assert.True(text.TextLayout.TextLines.Count > 1);
            foreach (var syllable in text.GetVisualDescendants().OfType<PinyinRubySyllable>())
            {
                var pronunciation = syllable.PronunciationText.TranslatePoint(default, text)!.Value;
                var original = syllable.OriginalText.TranslatePoint(default, text)!.Value;
                Assert.True(pronunciation.Y + syllable.PronunciationText.Bounds.Height <= original.Y + 0.5);
                Assert.InRange(Math.Abs(pronunciation.X + syllable.PronunciationText.Bounds.Width / 2 -
                    original.X - syllable.OriginalText.Bounds.Width / 2), 0, 0.5);
            }

            text.FontSize = 28;
            text.PinyinForeground = Brushes.DarkOrange;
            Pump(window);
            foreach (var syllable in text.GetVisualDescendants().OfType<PinyinRubySyllable>())
            {
                Assert.Equal(28, syllable.OriginalText.FontSize);
                Assert.Equal(10, syllable.PronunciationText.FontSize);
                Assert.Same(Brushes.DarkOrange, syllable.PronunciationText.Foreground);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>独立拼音行不改变原文字距，原文与拼音均在有限宽度内自主换行。</summary>
    [AvaloniaFact]
    public void SeparateLinePreservesOriginalLayoutAndSupportsHoverOnLaterLines()
    {
        const string content = "你好你好你好你好你好你好你好你好你好你好";
        var text = CreateText(content, PinyinLayoutMode.SeparateLine);
        text.Width = 125;
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            var presenter = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
            var original = presenter.OriginalText;
            var expected = new TextBlock { Text = content, FontSize = text.FontSize, FontFamily = text.FontFamily,
                TextWrapping = TextWrapping.Wrap };
            expected.Measure(new(original.Bounds.Width, double.PositiveInfinity));
            expected.Arrange(new Rect(expected.DesiredSize));
            Assert.True(original.TextLayout.TextLines.Count > 1);
            Assert.True(presenter.PronunciationText.TextLayout.TextLines.Count > 1);
            Assert.True(presenter.Bounds.Width <= text.Bounds.Width + 0.5);
            Assert.Equal(expected.TextLayout.TextLines.Select(line => line.Length),
                original.TextLayout.TextLines.Select(line => line.Length));
            for (var index = 0; index < content.Length; index++)
            {
                Assert.Equal(expected.TextLayout.HitTestTextPosition(index).Width,
                    original.TextLayout.HitTestTextPosition(index).Width, 3);
            }
            var laterIndex = original.TextLayout.TextLines[1].FirstTextSourceIndex + 1;
            window.MouseMove(FindHanziPoint(text, laterIndex, laterIndex, window));
            Pump(window);
            Assert.Equal(laterIndex, text.HighlightedSourceStart);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>表情占位与完整 emoji 簇不会使后续汉字的读音对应错位。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public async Task ImagesAndEmojiDoNotShiftFollowingHanziMappings(PinyinLayoutMode layout)
    {
        const string content = "A👩‍💻[笑哭]好\n你";
        var imagePath = Path.Combine(Path.GetTempPath(), $"pinyin-emoticon-{Guid.NewGuid():N}.png");
        using (var bitmap = new RenderTargetBitmap(new(8, 8), new(96, 96)))
        {
            bitmap.Save(imagePath);
        }
        var text = CreateText(content, layout);
        text.Emoticons = [new DanmuEmoticon { Text = "[笑哭]", ImagePath = imagePath }];
        var window = CreateWindow(text);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Single(text.GetVisualDescendants().OfType<Image>());
            var sourceIndex = content.IndexOf('好');
            var renderedIndex = sourceIndex - "[笑哭]".Length + 1;
            window.MouseMove(FindHanziPoint(text, sourceIndex, renderedIndex, window));
            Pump(window);
            Assert.Equal(sourceIndex, text.HighlightedSourceStart);
            AssertHighlightedPronunciation(text, "hǎo");
            if (layout == PinyinLayoutMode.Ruby)
            {
                Assert.Equal(2, text.GetVisualDescendants().OfType<PinyinRubySyllable>().Count());
            }
            else
            {
                var presenter = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
                Assert.DoesNotContain("xiào", presenter.PronunciationText.Inlines!.Text);
                Assert.DoesNotContain("kū", presenter.PronunciationText.Inlines.Text);
                var secondLineSource = content.IndexOf('你');
                window.MouseMove(FindHanziPoint(text, secondLineSource, secondLineSource - 3, window));
                Pump(window);
                Assert.Equal(secondLineSource, text.HighlightedSourceStart);
            }
        }
        finally
        {
            window.Close();
            File.Delete(imagePath);
        }
    }

    /// <summary>仅表情标记包含汉字时不会生成空注音行，图片缺失仍保留原始标记。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public async Task EmoticonOnlyContentDoesNotCreateAnEmptyPronunciationRow(PinyinLayoutMode layout)
    {
        var text = CreateText("[笑哭]", layout);
        text.Emoticons = [new DanmuEmoticon { Text = "[笑哭]", ImagePath = Path.Combine(Path.GetTempPath(),
            $"missing-pinyin-emoticon-{Guid.NewGuid():N}.png") }];
        var window = CreateWindow(text);
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.False(text.HasAnnotations);
            Assert.Empty(text.GetVisualDescendants().OfType<PinyinRubySyllable>());
            Assert.Empty(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
            Assert.Equal("[笑哭]", text.Inlines!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>悬停注音不会吞掉消息行的点击及右键菜单。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void AnnotationHoverPreservesParentButtonAndContextMenu(PinyinLayoutMode layout)
    {
        var text = CreateText("你好", layout);
        var button = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top, ContextMenu = new ContextMenu { Items = { new MenuItem { Header = "action" } } } };
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var window = CreateWindow(button);
        try
        {
            window.Show();
            Pump(window);
            var point = FindHanziPoint(text, 1, 1, window);
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump(window);
            Assert.Equal(1, clicks);
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Pump(window);
            Assert.True(button.ContextMenu.IsOpen);
        }
        finally
        {
            button.ContextMenu.Close();
            window.Close();
        }
    }

    /// <summary>内容、排版与容器复用后不会残留之前的高亮。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void ReplacingContentOrLayoutClearsPreviousHighlight(PinyinLayoutMode layout)
    {
        var text = CreateText("你好", layout);
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Assert.Equal(1, text.HighlightedSourceStart);
            text.PinyinLayout = layout == PinyinLayoutMode.Ruby ? PinyinLayoutMode.SeparateLine : PinyinLayoutMode.Ruby;
            Pump(window);
            Assert.Equal(-1, text.HighlightedSourceStart);
            window.MouseMove(new(290, 210));
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Assert.Equal(1, text.HighlightedSourceStart);
            text.DataContext = new object();
            Assert.Equal(-1, text.HighlightedSourceStart);
            text.ContentText = "好";
            text.Annotations = Annotate("好");
            Pump(window);
            Assert.Equal(-1, text.HighlightedSourceStart);
            text.Annotations = [];
            Pump(window);
            Assert.False(text.HasAnnotations);
            Assert.Equal("好", text.Text);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>注音控件脱离可视树时清除悬停，重新挂载后仍能正确高亮。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void HoverIgnoresDetachedInlineContentAndRecoversWhenReattached(PinyinLayoutMode layout)
    {
        var text = CreateText("你好", layout);
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            var point = FindHanziPoint(text, 1, 1, window);
            window.MouseMove(point);
            Assert.Equal(1, text.HighlightedSourceStart);
            var local = window.TranslatePoint(point, text)!.Value;
            var inlineCollection = text.Inlines!;
            var inlines = inlineCollection.ToArray();
            inlineCollection.Clear();
            Assert.True(text.HasAnnotations);
            Assert.Empty(text.GetVisualDescendants().OfType<PinyinRubySyllable>());
            Assert.Empty(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
            text.UpdateHighlight(local);
            Assert.Equal(-1, text.HighlightedSourceStart);

            inlineCollection.AddRange(inlines);
            Pump(window);
            AssertNoHighlightedPronunciations(text);
            window.MouseMove(new(290, 210));
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Assert.Equal(1, text.HighlightedSourceStart);
            AssertHighlightedPronunciation(text, "hǎo");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>正文省略导致注音单元被移出可视树时，悬停不会访问失效坐标。</summary>
    [AvaloniaFact]
    public void HoverIgnoresRubySyllablesRemovedByTextTrimming()
    {
        var text = CreateText("你好", PinyinLayoutMode.Ruby);
        text.Width = 10;
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            Assert.True(text.HasAnnotations);
            Assert.Empty(text.GetVisualDescendants().OfType<PinyinRubySyllable>());
            text.UpdateHighlight(default);
            Assert.Equal(-1, text.HighlightedSourceStart);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>整体缩放同时更新正文及拼音字号，并清理几何变化前的悬停定位。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void ScalingCenteredTextKeepsItsContentAndRebuildsHoverGeometry(PinyinLayoutMode layout)
    {
        var text = CreateText("你好", layout);
        text.TextAlignment = TextAlignment.Center;
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Assert.Equal(1, text.HighlightedSourceStart);
            text.FontSize = 30;
            text.PinyinFontSize = 15;
            Assert.Equal(-1, text.HighlightedSourceStart);
            Pump(window);
            Assert.Equal("你好", text.ContentText);
            if (layout == PinyinLayoutMode.Ruby)
            {
                Assert.Equal(2, text.GetVisualDescendants().OfType<PinyinRubySyllable>().Count());
                Assert.All(text.GetVisualDescendants().OfType<PinyinRubySyllable>(), syllable =>
                {
                    Assert.Equal(30, syllable.OriginalText.FontSize);
                    Assert.Equal(15, syllable.PronunciationText.FontSize);
                });
            }
            else
            {
                var presenter = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
                Assert.Equal(30, presenter.OriginalText.FontSize);
                Assert.Equal(15, presenter.PronunciationText.FontSize);
                Assert.Equal(TextAlignment.Center, presenter.OriginalText.TextAlignment);
            }
            window.MouseMove(new(290, 210));
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Pump(window);
            Assert.Equal(1, text.HighlightedSourceStart);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>切换主题后已高亮音节立即使用新主题的前景与背景资源。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void HighlightColorsFollowThemeChanges(PinyinLayoutMode layout)
    {
        var text = CreateText("你好", layout);
        text.PinyinHighlightForeground = null;
        text.PinyinHighlightBackground = null;
        var window = CreateWindow(text);
        window.RequestedThemeVariant = ThemeVariant.Light;
        try
        {
            window.Show();
            Pump(window);
            window.MouseMove(FindHanziPoint(text, 1, 1, window));
            Assert.Equal(1, text.HighlightedSourceStart);
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Pump(window);
            Assert.Equal(1, text.HighlightedSourceStart);
            var theme = text.GetValue(ThemeVariantScope.ActualThemeVariantProperty);
            var foreground = text.FindResource(theme, "AppPrimaryBrush");
            var background = text.FindResource(theme, "AppSidebarSelectedBrush");
            if (layout == PinyinLayoutMode.Ruby)
            {
                var syllable = Assert.Single(text.GetVisualDescendants().OfType<PinyinRubySyllable>(),
                    value => value.Annotation.SourceStart == 1);
                Assert.Same(foreground, syllable.PronunciationText.Foreground);
                Assert.Same(background, syllable.PronunciationText.Background);
            }
            else
            {
                var presenter = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
                var run = Assert.Single(presenter.PronunciationText.Inlines!.OfType<Run>(), value => value.Text == "hǎo");
                Assert.Same(foreground, run.Foreground);
                Assert.Same(background, run.Background);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>中英文、注音汉字与图片混排时共享正文基线，拼音只扩展基线上方空间。</summary>
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public async Task RubyLatinEmojiAndImagesShareTheOriginalTextBaseline(double scale)
    {
        const string content = "nano好   打call   👩‍💻[笑哭]你";
        var imagePath = Path.Combine(Path.GetTempPath(), $"pinyin-baseline-{Guid.NewGuid():N}.png");
        using (var bitmap = new RenderTargetBitmap(new(12, 12), new(96, 96)))
        {
            bitmap.Save(imagePath);
        }
        var text = CreateText(content, PinyinLayoutMode.Ruby);
        text.FontSize = 20 * scale;
        text.PinyinFontSize = 10 * scale;
        text.TextWrapping = TextWrapping.NoWrap;
        text.Emoticons = [new DanmuEmoticon { Text = "[笑哭]", ImagePath = imagePath }];
        var window = CreateWindow(text);
        window.Width = 720;
        window.Background = Brushes.White;
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var line = Assert.Single(text.TextLayout.TextLines);
            var expectedBaseline = line.Baseline;
            var syllables = text.GetVisualDescendants().OfType<PinyinRubySyllable>().ToArray();
            Assert.Equal(3, syllables.Length);
            foreach (var syllable in syllables)
            {
                var original = syllable.OriginalText;
                var actualBaseline = original.TranslatePoint(default, text)!.Value.Y + original.TextLayout.Baseline;
                Assert.True(Math.Abs(actualBaseline - expectedBaseline) <= 0.5,
                    $"{syllable.Annotation.Text}: actual baseline {actualBaseline}, line baseline {expectedBaseline}, offset {TextBlock.GetBaselineOffset(syllable)}, desired {syllable.DesiredSize}");
                Assert.Equal(0, TextBlock.GetBaselineOffset(original));
                Assert.Equal(0, TextBlock.GetBaselineOffset(syllable.PronunciationText));
            }
            var image = Assert.Single(text.GetVisualDescendants().OfType<Image>());
            var imageBottom = image.TranslatePoint(default, text)!.Value.Y + image.Bounds.Height;
            Assert.InRange(Math.Abs(imageBottom - expectedBaseline), 0, 0.5);
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(Path.GetTempPath(), $"danmaku-pinyin-ruby-mixed-{scale:0.0}.png"));
        }
        finally
        {
            window.Close();
            File.Delete(imagePath);
        }
    }

    /// <summary>拉伸至宽容器后，注音控件不会保留按自然宽度折行产生的多余高度。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby, 1.0)]
    [InlineData(PinyinLayoutMode.Ruby, 1.5)]
    [InlineData(PinyinLayoutMode.SeparateLine, 1.0)]
    [InlineData(PinyinLayoutMode.SeparateLine, 1.5)]
    public void StretchedContentDesiredHeightMatchesItsActualTextHeight(PinyinLayoutMode layout, double scale)
    {
        var text = CreateText("中文观众", layout);
        text.Annotations = [new(0, "中", "zhōng"), new(1, "文", "wén"), new(2, "观", "guān"), new(3, "众", "zhòng")];
        text.FontSize = 13 * scale;
        text.PinyinFontSize = 10 * scale;
        text.TextAlignment = TextAlignment.Center;
        var window = CreateWindow(text);
        window.Width = 600;
        try
        {
            window.Show();
            Pump(window);
            Assert.Single(text.TextLayout.TextLines);
            Assert.InRange(Math.Abs(text.DesiredSize.Height - text.TextLayout.Height), 0, 1);
            if (layout == PinyinLayoutMode.Ruby)
            {
                Assert.Equal(4, text.GetVisualDescendants().OfType<PinyinRubySyllable>().Count());
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>刚好装下两枚注音单元时保留同一行，实际不足的宽度仍正常换行。</summary>
    [AvaloniaTheory]
    [InlineData(40, 1)]
    [InlineData(39, 2)]
    public void ExactFitInlineObjectsDoNotOverflowTheirActualWidth(double width, int expectedLines)
    {
        var text = CreateText("你好", PinyinLayoutMode.Ruby);
        text.Width = width;
        var window = CreateWindow(text);
        try
        {
            window.Show();
            Pump(window);
            Assert.Equal(width, text.Bounds.Width);
            Assert.Equal(expectedLines, text.TextLayout.TextLines.Count);
            Assert.Equal(2, text.GetVisualDescendants().OfType<PinyinRubySyllable>().Count());
            Assert.InRange(Math.Abs(text.DesiredSize.Height - text.TextLayout.Height), 0, 1);
        }
        finally
        {
            window.Close();
        }
    }

    internal static IReadOnlyList<PinyinAnnotation> Annotate(string content)
    {
        var result = new List<PinyinAnnotation>();
        var elements = StringInfo.GetTextElementEnumerator(content);
        while (elements.MoveNext())
        {
            var value = elements.GetTextElement();
            result.Add(new(elements.ElementIndex, value, value switch
            {
                "你" => "nǐ", "好" => "hǎo", "吗" => "ma", "长" => "cháng", "笑" => "xiào", "哭" => "kū", "打" => "dǎ", _ => ""
            }));
        }
        return result;
    }

    internal static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static EmoticonTextBlock CreateText(string content, PinyinLayoutMode layout)
    {
        return new()
        {
            ContentText = content, Annotations = Annotate(content), PinyinLayout = layout,
            FontSize = 20, Foreground = Brushes.Black, PinyinForeground = Brushes.Gray,
            PinyinHighlightForeground = Brushes.Red, PinyinHighlightBackground = Brushes.Yellow,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top
        };
    }

    private static Window CreateWindow(Control content)
    {
        return new() { Width = 300, Height = 220, Content = new Border { Padding = new(10), Child = content } };
    }

    private static Point FindHanziPoint(EmoticonTextBlock text, int sourceStart, int renderedIndex, Window window)
    {
        if (text.PinyinLayout == PinyinLayoutMode.Ruby)
        {
            var syllable = Assert.Single(text.GetVisualDescendants().OfType<PinyinRubySyllable>(),
                value => value.Annotation.SourceStart == sourceStart);
            return syllable.OriginalText.TranslatePoint(new(syllable.OriginalText.Bounds.Width * 0.85,
                syllable.OriginalText.Bounds.Height / 2), window)!.Value;
        }
        var presenter = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
        var rectangle = presenter.OriginalText.TextLayout.HitTestTextPosition(renderedIndex);
        return presenter.OriginalText.TranslatePoint(new(rectangle.X + rectangle.Width * 0.85,
            rectangle.Y + rectangle.Height / 2), window)!.Value;
    }

    private static void AssertHighlightedPronunciation(EmoticonTextBlock text, string pronunciation)
    {
        if (text.PinyinLayout == PinyinLayoutMode.Ruby)
        {
            var highlighted = Assert.Single(text.GetVisualDescendants().OfType<PinyinRubySyllable>(),
                value => ReferenceEquals(value.PronunciationText.Background, Brushes.Yellow));
            Assert.Equal(pronunciation, highlighted.PronunciationText.Text);
            Assert.Same(Brushes.Red, highlighted.PronunciationText.Foreground);
            return;
        }
        var presenter = Assert.Single(text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>());
        var run = Assert.Single(presenter.PronunciationText.Inlines!.OfType<Run>(),
            value => ReferenceEquals(value.Background, Brushes.Yellow));
        Assert.Equal(pronunciation, run.Text);
        Assert.Same(Brushes.Red, run.Foreground);
    }

    private static void AssertNoHighlightedPronunciations(EmoticonTextBlock text)
    {
        Assert.All(text.GetVisualDescendants().OfType<PinyinRubySyllable>(),
            value => Assert.Null(value.PronunciationText.Background));
        foreach (var presenter in text.GetVisualDescendants().OfType<PinyinSeparateLinePresenter>())
        {
            Assert.All(presenter.PronunciationText.Inlines!.OfType<Run>(), value => Assert.Null(value.Background));
        }
    }
}
