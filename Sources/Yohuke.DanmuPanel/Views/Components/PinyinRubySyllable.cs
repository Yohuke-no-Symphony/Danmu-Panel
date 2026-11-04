using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class PinyinRubySyllable : StackPanel
{
    internal PinyinAnnotation Annotation { get; }
    internal TextBlock OriginalText { get; }
    internal TextBlock PronunciationText { get; }

    internal PinyinRubySyllable(PinyinAnnotation annotation)
    {
        Annotation = annotation;
        OriginalText = new()
        {
            Text = annotation.Text,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        PronunciationText = new()
        {
            Text = annotation.Pinyin,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        TextBlock.SetBaselineOffset(OriginalText, 0);
        TextBlock.SetBaselineOffset(PronunciationText, 0);
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
        PronunciationText.FontFamily = owner.FontFamily;
        PronunciationText.FontSize = owner.PinyinFontSize;
        PronunciationText.FontWeight = FontWeight.Normal;
        PronunciationText.Foreground = pronunciationForeground;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var desiredSize = base.MeasureOverride(availableSize);
        TextBlock.SetBaselineOffset(this, PronunciationText.DesiredSize.Height + OriginalText.TextLayout.Baseline);
        return desiredSize;
    }
}
