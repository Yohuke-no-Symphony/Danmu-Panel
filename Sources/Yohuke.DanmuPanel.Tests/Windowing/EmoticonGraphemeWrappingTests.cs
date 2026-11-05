using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证 Ruby 内联注音和复合字素在窄行换行时保留完整原文。</summary>
public sealed class EmoticonGraphemeWrappingTests
{
    /// <summary>家庭 emoji、变体选择符和组合音标均作为完整字素换行，不触发簇内拆分。</summary>
    [AvaloniaTheory]
    [InlineData("👨‍👩‍👧‍👦", 13)]
    [InlineData("👨‍👩‍👧‍👦", 20)]
    [InlineData("❤️", 13)]
    [InlineData("e\u0301", 13)]
    public void CompositeGraphemesRemainIntactAcrossNarrowRubyWidths(string grapheme, double fontSize)
    {
        var content = "你好" + grapheme + "好你";
        var text = new EmoticonTextBlock
        {
            ContentText = content,
            Annotations = PinyinTextRenderingTests.Annotate(content),
            PinyinLayout = PinyinLayoutMode.Ruby,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            FontSize = fontSize,
            PinyinFontSize = 10,
            Width = 160
        };
        var window = new Window { Width = 240, Height = 320, Content = text };
        try
        {
            window.Show();
            for (var width = 160; width >= 1; width--)
            {
                text.Width = width;
                PinyinTextRenderingTests.Pump(window);
                Assert.Equal(4, text.GetVisualDescendants().OfType<PinyinRubySyllable>().Count());
                Assert.NotEmpty(text.TextLayout.TextLines);
                Assert.Equal(content, string.Concat(text.Inlines!.Select(inline => inline switch
                {
                    Run run => run.Text,
                    InlineUIContainer { Child: PinyinRubySyllable syllable } => syllable.Annotation.Text,
                    InlineUIContainer { Child: TextBlock element } => element.Text,
                    _ => ""
                })));
            }
        }
        finally
        {
            window.Close();
        }
    }
}
