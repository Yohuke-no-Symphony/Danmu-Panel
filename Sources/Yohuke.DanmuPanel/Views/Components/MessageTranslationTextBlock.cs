using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageTranslationTextBlock : EmoticonTextBlock
{
    private readonly TextBlock textMetrics = new() { Text = "\u200B" };

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public MessageTranslationTextBlock()
    {
        UpdateLineHeight();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty ||
            change.Property == FontStyleProperty || change.Property == FontWeightProperty ||
            change.Property == FontStretchProperty || change.Property == ReplyProperty)
        {
            UpdateLineHeight();
        }
    }

    private void UpdateLineHeight()
    {
        textMetrics.FontFamily = FontFamily;
        textMetrics.FontSize = FontSize;
        textMetrics.FontStyle = FontStyle;
        textMetrics.FontWeight = FontWeight;
        textMetrics.FontStretch = FontStretch;
        textMetrics.Measure(Size.Infinity);
        SetCurrentValue(LineHeightProperty, Reply is null ? textMetrics.TextLayout.Height : double.NaN);
    }
}
