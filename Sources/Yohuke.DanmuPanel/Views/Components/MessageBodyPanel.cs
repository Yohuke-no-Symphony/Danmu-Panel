using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageBodyPanel : Panel
{
    private readonly List<Layoutable> invalidatedTextMeasures = [];
    private Control original = null!;
    private Control translation = null!;
    private Func<double> getOriginalBaseline = null!;
    private Func<double> getTranslationBaseline = null!;
    private Func<double> getOriginalInlineWidth = null!;
    private Func<bool> canShareLine = null!;
    private IUserAvatarService? avatars;
    private EmoticonBitmapCache? bitmapCache;
    private bool isInline;
    private double originalWidth;
    private double translationX;
    private double originalBaseline;
    private double translationBaseline;
    private double inlineBaseline;

    public static readonly StyledProperty<bool> AllowInlineProperty =
        AvaloniaProperty.Register<MessageBodyPanel, bool>(nameof(AllowInline));

    public static readonly StyledProperty<bool> UseMarqueeProperty =
        AvaloniaProperty.Register<MessageBodyPanel, bool>(nameof(UseMarquee));

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

    public bool UseMarquee
    {
        get => GetValue(UseMarqueeProperty);
        set => SetValue(UseMarqueeProperty, value);
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
        AffectsMeasure<MessageBodyPanel>(AllowInlineProperty, UseMarqueeProperty, HorizontalSpacingProperty, VerticalSpacingProperty);
    }

    public MessageBodyPanel()
    {
        AvaloniaXamlLoader.Load(this);
        var originalHost = this.FindControl<MessageTemplateHost>("OriginalHost")!;
        var translationHost = this.FindControl<MessageTemplateHost>("TranslationHost")!;
        var pendingTranslationHost = this.FindControl<MessageTemplateHost>("PendingTranslationHost")!;
        var contentPinyinHost = this.FindControl<MessageTemplateHost>("ContentPinyinHost")!;
        InitializeMetrics(this.FindControl<StackPanel>("OriginalRegion")!, this.FindControl<Grid>("TranslationRegion")!,
            () => contentPinyinHost.DesiredSize.Height + GetBaseline(originalHost.Child),
            () => GetBaseline(ActiveTranslation()),
            () => originalHost.Child switch
            {
                EmoticonTextBlock text => text.InlineTextWidth,
                HoverMarqueeTextBlock marquee => marquee.InlineTextWidth,
                _ => 0
            },
            () => IsSingleLine(originalHost.Child) && IsSingleLine(ActiveTranslation()));
        ObserveTemplate(originalHost);
        ObserveTemplate(translationHost);
        ObserveTemplate(pendingTranslationHost);
        ObserveTemplate(contentPinyinHost);

        Control? ActiveTranslation()
        {
            return pendingTranslationHost.Child ?? translationHost.Child;
        }
    }

    internal MessageBodyPanel(Control original, Control translation, Func<double> getOriginalBaseline,
        Func<double> getTranslationBaseline, Func<double> getOriginalInlineWidth, Func<bool> canShareLine)
    {
        InitializeMetrics(original, translation, getOriginalBaseline, getTranslationBaseline, getOriginalInlineWidth, canShareLine);
        Children.Add(original);
        Children.Add(translation);
    }

    private void InitializeMetrics(Control originalRegion, Control translationRegion, Func<double> originalBaselineProvider,
        Func<double> translationBaselineProvider, Func<double> originalInlineWidthProvider, Func<bool> canShareLineProvider)
    {
        original = originalRegion;
        translation = translationRegion;
        getOriginalBaseline = originalBaselineProvider;
        getTranslationBaseline = translationBaselineProvider;
        getOriginalInlineWidth = originalInlineWidthProvider;
        canShareLine = canShareLineProvider;
    }

    internal void SetServices(IUserAvatarService? avatars, EmoticonBitmapCache? bitmapCache)
    {
        this.avatars = avatars;
        this.bitmapCache = bitmapCache;
    }

    private static double GetBaseline(Control? control)
    {
        return control switch
        {
            EmoticonTextBlock text => text.Padding.Top + text.TextLayout.Baseline,
            HoverMarqueeTextBlock marquee => marquee.TextBaseline,
            ShimmerTextBlock placeholder => placeholder.TextBaseline,
            _ => 0
        };
    }

    private static bool IsSingleLine(Control? control)
    {
        return control switch
        {
            EmoticonTextBlock text => text.IsSingleTextLine,
            HoverMarqueeTextBlock marquee => marquee.IsSingleTextLine,
            ShimmerTextBlock placeholder => placeholder.IsSingleTextLine,
            _ => false
        };
    }

    private void ObserveTemplate(MessageTemplateHost host)
    {
        host.PreparingChild += PrepareChild;
        host.RemovingChild += RemoveChild;

        void PrepareChild(Control child)
        {
            switch (child)
            {
                case EmoticonTextBlock text:
                    text.AvatarService = avatars;
                    if (text is MessageTranslationTextBlock)
                    {
                        ObserveText(text);
                    }
                    else
                    {
                        text.BitmapCache = bitmapCache;
                    }
                    text.AnnotationLayoutChanged += OnAnnotationLayoutChanged;
                    break;
                case HoverMarqueeTextBlock marquee:
                    marquee.AvatarService = avatars;
                    marquee.BitmapCache = bitmapCache;
                    marquee.TextMetricsChanged += InvalidateTextMetrics;
                    break;
                default:
                    ObserveText(child);
                    break;
            }
        }

        void RemoveChild(Control child)
        {
            switch (child)
            {
                case EmoticonTextBlock text:
                    text.AnnotationLayoutChanged -= OnAnnotationLayoutChanged;
                    if (text is MessageTranslationTextBlock)
                    {
                        text.PropertyChanged -= OnTextPropertyChanged;
                    }
                    break;
                case HoverMarqueeTextBlock marquee:
                    marquee.TextMetricsChanged -= InvalidateTextMetrics;
                    break;
                default:
                    child.PropertyChanged -= OnTextPropertyChanged;
                    break;
            }
        }

        void OnAnnotationLayoutChanged()
        {
            if (host.Child is { } child)
            {
                InvalidateTextMetrics(child);
            }
        }
    }

    internal void ObserveText(Control text)
    {
        text.PropertyChanged += OnTextPropertyChanged;
    }

    internal void InvalidateTextMetrics(Control text)
    {
        for (Layoutable? current = text; current is not null && !ReferenceEquals(current, this);
             current = current.GetVisualParent<Layoutable>())
        {
            if (invalidatedTextMeasures.Contains(current))
            {
                if (!current.IsMeasureValid)
                {
                    break;
                }
            }
            else
            {
                invalidatedTextMeasures.Add(current);
            }
            current.InvalidateMeasure();
        }
        if (IsMeasureValid)
        {
            InvalidateMeasure();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        invalidatedTextMeasures.Clear();
        var constraint = new Size(availableSize.Width, double.PositiveInfinity);
        original.Measure(constraint);
        translation.Measure(constraint);
        isInline = false;
        if (AllowInline && original.IsVisible && translation.IsVisible)
        {
            originalWidth = original.DesiredSize.Width;
            translationX = getOriginalInlineWidth() + HorizontalSpacing;
            var width = translationX + translation.DesiredSize.Width;
            if (width <= availableSize.Width && canShareLine())
            {
                isInline = true;
                originalBaseline = getOriginalBaseline();
                translationBaseline = getTranslationBaseline();
                inlineBaseline = Math.Max(originalBaseline, translationBaseline);
                var belowBaseline = Math.Max(original.DesiredSize.Height - originalBaseline,
                    translation.DesiredSize.Height - translationBaseline);
                return new(Math.Max(originalWidth, width), inlineBaseline + belowBaseline);
            }
        }

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

    private void OnTextPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == TextBlock.TextProperty || change.Property == TextBlock.FontFamilyProperty ||
            change.Property == TextBlock.FontSizeProperty || change.Property == TextBlock.FontWeightProperty ||
            change.Property == TextBlock.FontStyleProperty || change.Property == TextBlock.FontStretchProperty ||
            change.Property == TextBlock.LineHeightProperty || change.Property == TextBlock.LineSpacingProperty ||
            change.Property == TextBlock.LetterSpacingProperty || change.Property == TextBlock.MaxLinesProperty ||
            change.Property == TextBlock.TextWrappingProperty || change.Property == TextBlock.TextTrimmingProperty ||
            change.Property == TextBlock.TextAlignmentProperty || change.Property == TextBlock.PaddingProperty ||
            change.Property == FlowDirectionProperty || change.Property == IsVisibleProperty)
        {
            InvalidateTextMetrics((Control)sender!);
        }
    }
}
