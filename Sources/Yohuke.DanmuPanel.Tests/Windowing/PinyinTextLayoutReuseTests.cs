using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证逐字注音不在排列阶段重复塑形，并保留字号、复用及受约束排版语义。</summary>
public sealed class PinyinTextLayoutReuseTests(ITestOutputHelper output)
{
    /// <summary>汉字与拼音在测量后沿用同一布局，居中和正文基线仍与实际文字一致。</summary>
    [AvaloniaTheory]
    [InlineData("好", "hǎo")]
    [InlineData("长", "cháng")]
    public void RubyArrangementReusesTheMeasuredLayoutsAndPreservesItsBaseline(string original, string pinyin)
    {
        var syllable = new PinyinRubySyllable(new(0, original, pinyin));
        syllable.UpdateTypography(new() { FontSize = 20, PinyinFontSize = 10 }, Brushes.Gray);
        syllable.Measure(new(200, double.PositiveInfinity));
        var originalLayout = syllable.OriginalText.TextLayout;
        var pronunciationLayout = syllable.PronunciationText.TextLayout;

        syllable.Arrange(new(0, 0, 200, syllable.DesiredSize.Height));

        Assert.Same(originalLayout, syllable.OriginalText.TextLayout);
        Assert.Same(pronunciationLayout, syllable.PronunciationText.TextLayout);
        Assert.InRange(Math.Abs(100 - syllable.OriginalText.Bounds.Center.X), 0, 0.5);
        Assert.InRange(Math.Abs(100 - syllable.PronunciationText.Bounds.Center.X), 0, 0.5);
        Assert.Equal(syllable.PronunciationText.DesiredSize.Height + originalLayout.Baseline,
            TextBlock.GetBaselineOffset(syllable), 3);
    }

    /// <summary>在相同字形持续换绑的负载下，保留测量布局减少原生排列重复塑形的分配。</summary>
    [AvaloniaFact]
    public void RebindingSingleLineGlyphsAllocatesLessThanNativeTextBlocks()
    {
        var native = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.NoWrap };
        var optimized = new PinyinGlyphTextBlock { FontSize = 20, TextWrapping = TextWrapping.NoWrap };
        MeasureGlyphs(native, 20);
        MeasureGlyphs(optimized, 20);

        var nativeBytes = MeasureGlyphs(native, 1000);
        var optimizedBytes = MeasureGlyphs(optimized, 1000);

        output.WriteLine($"1000 glyph rebindings: native={nativeBytes} bytes; reused={optimizedBytes} bytes.");
        Assert.True(optimizedBytes < nativeBytes);
        Assert.Equal(native.TextLayout.Height, optimized.TextLayout.Height, 3);
        Assert.Equal(native.TextLayout.Baseline, optimized.TextLayout.Baseline, 3);
        Assert.Equal(native.TextLayout.HitTestTextPosition(0), optimized.TextLayout.HitTestTextPosition(0));
    }

    /// <summary>回收后的文字、字号与颜色变化会更新真实布局，不沿用旧消息或旧字体。</summary>
    [AvaloniaFact]
    public void RebindingAndTypographyChangesReplaceTheMeasuredGlyphs()
    {
        var owner = new EmoticonTextBlock { FontSize = 20, PinyinFontSize = 10, Foreground = Brushes.Black };
        var syllable = new PinyinRubySyllable(new(0, "好", "hǎo"));
        syllable.UpdateTypography(owner, Brushes.Gray);
        Layout(syllable);
        var original = syllable.OriginalText.TextLayout;
        var pronunciation = syllable.PronunciationText.TextLayout;

        syllable.UpdateAnnotation(new(8, "长", "cháng"));
        owner.FontSize = 30;
        owner.PinyinFontSize = 15;
        owner.FontWeight = FontWeight.Bold;
        owner.Foreground = Brushes.DarkBlue;
        syllable.UpdateTypography(owner, Brushes.DarkOrange);
        Layout(syllable);

        Assert.NotSame(original, syllable.OriginalText.TextLayout);
        Assert.NotSame(pronunciation, syllable.PronunciationText.TextLayout);
        Assert.Equal("长", syllable.OriginalText.Text);
        Assert.Equal("cháng", syllable.PronunciationText.Text);
        Assert.Equal(8, syllable.Annotation.SourceStart);
        Assert.Equal(30, syllable.OriginalText.FontSize);
        Assert.Equal(15, syllable.PronunciationText.FontSize);
        Assert.Equal(FontWeight.Bold, syllable.OriginalText.FontWeight);
        Assert.Equal(FontWeight.Normal, syllable.PronunciationText.FontWeight);
        Assert.Same(Brushes.DarkBlue, syllable.OriginalText.Foreground);
        Assert.Same(Brushes.DarkOrange, syllable.PronunciationText.Foreground);
        Assert.True(syllable.OriginalText.TextLayout.Height > original.Height);
    }

    /// <summary>换行、居中、省略与从右到左的排版继续遵循原生文字约束。</summary>
    [AvaloniaTheory]
    [InlineData("wrap")]
    [InlineData("center")]
    [InlineData("trim")]
    [InlineData("rtl")]
    public void ConstrainedGlyphLayoutsMatchTheNativeTextBlock(string mode)
    {
        var syllable = new PinyinRubySyllable(new(0, "好", "hǎo"));
        var actual = syllable.OriginalText;
        var expected = new TextBlock();
        foreach (var text in new[] { actual, expected })
        {
            text.Text = "hello 你好 world";
            text.FontSize = 20;
            text.TextWrapping = mode == "wrap" ? TextWrapping.Wrap : TextWrapping.NoWrap;
            text.TextAlignment = mode == "center" ? TextAlignment.Center : TextAlignment.Left;
            text.TextTrimming = mode == "trim" ? TextTrimming.CharacterEllipsis : TextTrimming.None;
            text.FlowDirection = mode == "rtl" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            text.HorizontalAlignment = HorizontalAlignment.Stretch;
            text.Measure(new(80, double.PositiveInfinity));
            text.Arrange(new(0, 0, 80, text.DesiredSize.Height));
        }

        Assert.Equal(expected.DesiredSize, actual.DesiredSize);
        Assert.Equal(expected.TextLayout.Height, actual.TextLayout.Height, 3);
        Assert.Equal(expected.TextLayout.Baseline, actual.TextLayout.Baseline, 3);
        Assert.Equal(expected.TextLayout.TextLines.Select(line => (line.Length, line.Start, line.HasCollapsed)),
            actual.TextLayout.TextLines.Select(line => (line.Length, line.Start, line.HasCollapsed)));
    }

    private static void Layout(PinyinRubySyllable syllable)
    {
        syllable.Measure(new(200, double.PositiveInfinity));
        syllable.Arrange(new Rect(syllable.DesiredSize));
    }

    private static long MeasureGlyphs(TextBlock text, int count)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++)
        {
            text.Text = index % 2 == 0 ? "好" : "长";
            text.Measure(new(200, double.PositiveInfinity));
            text.Arrange(new Rect(text.DesiredSize));
        }
        return GC.GetAllocatedBytesForCurrentThread() - allocated;
    }
}
