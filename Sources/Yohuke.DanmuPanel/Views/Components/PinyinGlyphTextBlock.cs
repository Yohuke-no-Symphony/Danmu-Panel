using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class PinyinGlyphTextBlock : TextBlock
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <summary>纯文本单行的左对齐布局与排列宽度无关，沿用测量结果避免重复塑形。</summary>
    /// <remarks>仅复用能完整装入的从左到右单行；换行、省略、对齐和内联对象继续由原生排列更新约束。</remarks>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (TextWrapping == TextWrapping.NoWrap && TextTrimming == TextTrimming.None &&
            TextAlignment is TextAlignment.Left or TextAlignment.Start && FlowDirection == FlowDirection.LeftToRight &&
            MaxLines == 0 && Padding == default && Inlines is not { Count: > 0 })
        {
            var layout = TextLayout;
            if (layout.TextLines.Count == 1 && layout.WidthIncludingTrailingWhitespace <= finalSize.Width &&
                layout.Height <= finalSize.Height)
            {
                return finalSize;
            }
        }
        return base.ArrangeOverride(finalSize);
    }
}
