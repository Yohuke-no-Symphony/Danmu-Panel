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
        SetCurrentValue(LineHeightProperty, Reply is null ? MeasureNaturalLineHeight(textMetrics, this) : double.NaN);
    }

    internal static double MeasureNaturalLineHeight(TextBlock metrics, AvaloniaObject owner)
    {
        metrics.FontFamily = owner.GetValue(FontFamilyProperty);
        metrics.FontSize = owner.GetValue(FontSizeProperty);
        metrics.FontStyle = owner.GetValue(FontStyleProperty);
        metrics.FontWeight = owner.GetValue(FontWeightProperty);
        metrics.FontStretch = owner.GetValue(FontStretchProperty);
        metrics.Measure(Size.Infinity);
        return metrics.TextLayout.Height;
    }
}
