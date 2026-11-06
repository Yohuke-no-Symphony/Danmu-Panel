using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageBodyPanel : Panel
{
    private readonly Control original;
    private readonly Control translation;
    private readonly Func<double> getOriginalBaseline;
    private readonly Func<double> getTranslationBaseline;
    private bool isInline;
    private double originalWidth;
    private double originalBaseline;
    private double translationBaseline;
    private double inlineBaseline;

    public static readonly StyledProperty<bool> AllowInlineProperty =
        AvaloniaProperty.Register<MessageBodyPanel, bool>(nameof(AllowInline));

    public static readonly StyledProperty<double> HorizontalSpacingProperty =
        AvaloniaProperty.Register<MessageBodyPanel, double>(nameof(HorizontalSpacing),
            validate: value => double.IsFinite(value) && value >= 0);

    public static readonly StyledProperty<double> VerticalSpacingProperty =
        AvaloniaProperty.Register<MessageBodyPanel, double>(nameof(VerticalSpacing),
            validate: value => double.IsFinite(value) && value >= 0);

    public bool AllowInline
    {
        get => GetValue(AllowInlineProperty);
        set => SetValue(AllowInlineProperty, value);
    }

    public double HorizontalSpacing
    {
        get => GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    static MessageBodyPanel()
    {
        AffectsMeasure<MessageBodyPanel>(AllowInlineProperty, HorizontalSpacingProperty, VerticalSpacingProperty);
    }

    internal MessageBodyPanel(Control original, Control translation, Func<double> getOriginalBaseline,
        Func<double> getTranslationBaseline)
    {
        this.original = original;
        this.translation = translation;
        this.getOriginalBaseline = getOriginalBaseline;
        this.getTranslationBaseline = getTranslationBaseline;
        Children.Add(original);
        Children.Add(translation);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        isInline = false;
        if (AllowInline && original.IsVisible && translation.IsVisible)
        {
            original.Measure(Size.Infinity);
            originalWidth = original.DesiredSize.Width;
            if (originalWidth + HorizontalSpacing <= availableSize.Width)
            {
                translation.Measure(Size.Infinity);
                var width = originalWidth + HorizontalSpacing + translation.DesiredSize.Width;
                if (width <= availableSize.Width)
                {
                    isInline = true;
                    originalBaseline = getOriginalBaseline();
                    translationBaseline = getTranslationBaseline();
                    inlineBaseline = Math.Max(originalBaseline, translationBaseline);
                    var belowBaseline = Math.Max(original.DesiredSize.Height - originalBaseline,
                        translation.DesiredSize.Height - translationBaseline);
                    return new(width, inlineBaseline + belowBaseline);
                }
            }
        }

        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        original.Measure(constraint);
        translation.Measure(constraint);
        return new(Math.Max(original.DesiredSize.Width, translation.DesiredSize.Width),
            original.DesiredSize.Height + translation.DesiredSize.Height + GetVerticalSpacing());
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (isInline)
        {
            original.Arrange(new(0, inlineBaseline - originalBaseline,
                originalWidth, original.DesiredSize.Height));
            var translationX = originalWidth + HorizontalSpacing;
            translation.Arrange(new(translationX, inlineBaseline - translationBaseline,
                Math.Max(0, finalSize.Width - translationX), translation.DesiredSize.Height));
        }
        else
        {
            original.Arrange(new(0, 0, finalSize.Width, original.DesiredSize.Height));
            translation.Arrange(new(0, original.DesiredSize.Height + GetVerticalSpacing(),
                finalSize.Width, translation.DesiredSize.Height));
        }
        return finalSize;
    }

    private double GetVerticalSpacing()
    {
        return original.IsVisible && translation.IsVisible ? VerticalSpacing : 0;
    }
}
