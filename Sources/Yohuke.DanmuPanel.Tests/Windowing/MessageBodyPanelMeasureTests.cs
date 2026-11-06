using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;
using Xunit.Abstractions;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证连续改变正文宽度时及时换行，且每段文字只进行必要的测量。</summary>
public sealed class MessageBodyPanelMeasureTests(ITestOutputHelper output)
{
    /// <summary>持续缩窄已换行的译文，不反复按自然宽度重新排版原文和译文。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResizingWrappedTranslationHasBoundedMeasurementWork(bool longOriginal)
    {
        var originalText = new TextBlock
        {
            Text = longOriginal ? string.Concat(Enumerable.Repeat("原文需要自动换行。", 20)) : "原文",
            TextWrapping = TextWrapping.Wrap
        };
        var translationText = new TextBlock
        {
            Text = string.Concat(Enumerable.Repeat("The translated message needs wrapping. ", 20)),
            TextWrapping = TextWrapping.Wrap
        };
        var original = new LayoutCountingDecorator { Child = originalText };
        var translation = new LayoutCountingDecorator { Child = translationText };
        var panel = new MessageBodyPanel(original, translation,
            () => originalText.TextLayout.Baseline, () => translationText.TextLayout.Baseline,
            () => EmoticonTextBlock.HasSingleTextLine(originalText) && EmoticonTextBlock.HasSingleTextLine(translationText))
        {
            AllowInline = true, HorizontalSpacing = 8, VerticalSpacing = 2
        };
        panel.Measure(new(320, double.PositiveInfinity));
        panel.Arrange(new(panel.DesiredSize));
        var initialOriginal = original.MeasurePasses;
        var initialTranslation = translation.MeasurePasses;
        var initialInfinite = original.InfiniteWidthMeasurePasses + translation.InfiniteWidthMeasurePasses;
        var initialHeight = translation.Bounds.Height;
        const int STEPS = 80;
        for (var index = 0; index < STEPS; index++)
        {
            var width = 318 - index * 1.5;
            panel.Measure(new(width, double.PositiveInfinity));
            panel.Arrange(new(0, 0, width, panel.DesiredSize.Height));
            Assert.True(translationText.TextLayout.TextLines.Count > 1);
            Assert.True(translation.Bounds.Y >= original.Bounds.Bottom);
            Assert.InRange(Math.Abs(translation.Bounds.Height - translationText.DesiredSize.Height), 0, 1);
        }
        var originalPasses = original.MeasurePasses - initialOriginal;
        var translationPasses = translation.MeasurePasses - initialTranslation;
        var infinitePasses = original.InfiniteWidthMeasurePasses + translation.InfiniteWidthMeasurePasses - initialInfinite;
        output.WriteLine($"{STEPS} width changes: original={originalPasses}, translation={translationPasses}, infinite={infinitePasses}");
        Assert.True(translation.Bounds.Height > initialHeight);
        Assert.InRange(originalPasses, 1, STEPS + 2);
        Assert.InRange(translationPasses, 1, STEPS + 2);
        Assert.Equal(0, infinitePasses);
    }

    /// <summary>折行后每一行虽短，整个文本仍不能与另一段文字并排。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrappedWordsAreNotMistakenForShortSingleLineContent(bool originalWraps)
    {
        var original = new TextBlock { Text = originalWraps ? "MMMMMMM MMMMMMM" : "x", FontSize = 16, TextWrapping = TextWrapping.Wrap };
        var translation = new TextBlock { Text = originalWraps ? "x" : "MMMMMMM MMMMMMM", FontSize = 16, TextWrapping = TextWrapping.Wrap };
        var panel = new MessageBodyPanel(original, translation,
            () => original.TextLayout.Baseline, () => translation.TextLayout.Baseline,
            () => EmoticonTextBlock.HasSingleTextLine(original) && EmoticonTextBlock.HasSingleTextLine(translation))
        {
            AllowInline = true, HorizontalSpacing = 8, VerticalSpacing = 2
        };
        const double WIDTH = 150;
        panel.Measure(new(WIDTH, double.PositiveInfinity));
        panel.Arrange(new(0, 0, WIDTH, panel.DesiredSize.Height));

        Assert.True(original.DesiredSize.Width + translation.DesiredSize.Width + panel.HorizontalSpacing <= WIDTH);
        Assert.True((originalWraps ? original : translation).TextLayout.TextLines.Count > 1);
        Assert.Equal(0, translation.Bounds.X);
        Assert.True(translation.Bounds.Y >= original.Bounds.Bottom);
    }

    /// <summary>逐字和整行注音都依据实际内部文字折行情况判断能否同行。</summary>
    [AvaloniaTheory]
    [InlineData(PinyinLayoutMode.Ruby)]
    [InlineData(PinyinLayoutMode.SeparateLine)]
    public void AnnotatedContentReportsItsActualWrappedLines(PinyinLayoutMode layout)
    {
        const string CONTENT = "MMMMMMM晚 MMMMMMM晚";
        var text = new EmoticonTextBlock
        {
            ContentText = CONTENT, Annotations = new PinyinService().GetAnnotations(CONTENT),
            PinyinLayout = layout, TextWrapping = TextWrapping.Wrap, FontSize = 16, Width = 150
        };
        var window = new Window { Width = 200, Height = 300, Content = text };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.False(text.IsSingleTextLine);
        }
        finally
        {
            window.Close();
        }
    }
}
