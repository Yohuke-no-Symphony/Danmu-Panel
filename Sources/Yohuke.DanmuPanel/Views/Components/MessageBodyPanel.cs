using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageBodyPanel : Panel
{
    private Control original = null!;
    private Control translation = null!;
    private Func<double> getOriginalBaseline = null!;
    private Func<double> getTranslationBaseline = null!;
    private Func<double> getOriginalInlineWidth = null!;
    private Func<bool> canShareLine = null!;
    private bool isInline;
    private double originalWidth;
    private double translationX;
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

    public MessageBodyPanel()
    {
        AvaloniaXamlLoader.Load(this);
        var originalText = this.FindControl<EmoticonTextBlock>("OriginalText")!;
        var originalMarquee = this.FindControl<HoverMarqueeTextBlock>("OriginalMarquee")!;
        var translatedText = this.FindControl<MessageTranslationTextBlock>("TranslatedText")!;
        var translationMarquee = this.FindControl<HoverMarqueeTextBlock>("TranslationMarquee")!;
        var pendingTranslation = this.FindControl<ShimmerTextBlock>("PendingTranslation")!;
        var contentPending = this.FindControl<PinyinLoadingPlaceholder>("ContentPinyinPending")!;
        InitializeMetrics(this.FindControl<StackPanel>("OriginalRegion")!, this.FindControl<Grid>("TranslationRegion")!,
            () => contentPending.DesiredSize.Height + (originalText.IsVisible
                ? originalText.Padding.Top + originalText.TextLayout.Baseline : originalMarquee.TextBaseline),
            () => pendingTranslation.IsVisible ? pendingTranslation.TextBaseline : translatedText.IsVisible
                ? translatedText.Padding.Top + translatedText.TextLayout.Baseline : translationMarquee.TextBaseline,
            () => originalText.IsVisible ? originalText.InlineTextWidth : originalMarquee.InlineTextWidth,
            () => (originalText.IsVisible ? originalText.IsSingleTextLine : originalMarquee.IsSingleTextLine) &&
                (pendingTranslation.IsVisible ? pendingTranslation.IsSingleTextLine : translatedText.IsVisible
                    ? EmoticonTextBlock.HasSingleTextLine(translatedText) : translationMarquee.IsSingleTextLine));
        originalText.AnnotationLayoutChanged += () => InvalidateTextMetrics(originalText);
        translatedText.AnnotationLayoutChanged += () => InvalidateTextMetrics(translatedText);
        originalMarquee.TextMetricsChanged += InvalidateTextMetrics;
        translationMarquee.TextMetricsChanged += InvalidateTextMetrics;
        ObserveText(translatedText);
        ObserveText(pendingTranslation);
        ObserveText(contentPending);
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
        this.FindControl<EmoticonTextBlock>("OriginalText")!.BitmapCache = bitmapCache;
        this.FindControl<HoverMarqueeTextBlock>("OriginalMarquee")!.BitmapCache = bitmapCache;
        this.FindControl<EmoticonTextBlock>("OriginalText")!.AvatarService = avatars;
        this.FindControl<HoverMarqueeTextBlock>("OriginalMarquee")!.AvatarService = avatars;
        this.FindControl<MessageTranslationTextBlock>("TranslatedText")!.AvatarService = avatars;
        this.FindControl<HoverMarqueeTextBlock>("TranslationMarquee")!.AvatarService = avatars;
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
            current.InvalidateMeasure();
        }
        InvalidateMeasure();
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
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
