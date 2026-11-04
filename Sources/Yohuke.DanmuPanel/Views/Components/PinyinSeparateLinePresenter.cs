using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class PinyinSeparateLinePresenter : StackPanel
{
    internal TextBlock OriginalText { get; }
    internal TextBlock PronunciationText { get; }

    internal PinyinSeparateLinePresenter(InlineCollection original, InlineCollection pronunciation)
    {
        OriginalText = new() { Inlines = original };
        PronunciationText = new() { Inlines = pronunciation };
        Children.Add(PronunciationText);
        Children.Add(OriginalText);
    }

    internal void UpdateTypography(EmoticonTextBlock owner, IBrush? pronunciationForeground)
    {
        OriginalText.FontFamily = owner.FontFamily;
        OriginalText.FontSize = owner.FontSize;
        OriginalText.FontWeight = owner.FontWeight;
        OriginalText.FontStyle = owner.FontStyle;
        OriginalText.FontStretch = owner.FontStretch;
        OriginalText.Foreground = owner.Foreground;
        OriginalText.TextWrapping = owner.TextWrapping;
        OriginalText.TextAlignment = owner.TextAlignment;
        OriginalText.LineSpacing = owner.LineSpacing;
        OriginalText.LineHeight = owner.LineHeight;
        OriginalText.TextTrimming = owner.TextTrimming;
        PronunciationText.FontFamily = owner.FontFamily;
        PronunciationText.FontSize = owner.PinyinFontSize;
        PronunciationText.FontWeight = FontWeight.Normal;
        PronunciationText.Foreground = pronunciationForeground;
        PronunciationText.TextWrapping = owner.TextWrapping;
        PronunciationText.TextAlignment = owner.TextAlignment;
    }

}
