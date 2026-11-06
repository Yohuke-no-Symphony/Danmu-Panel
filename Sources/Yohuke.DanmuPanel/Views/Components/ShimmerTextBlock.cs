using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>在可见文字上循环扫过高亮，并在隐藏或离开视口时停止动画。</summary>
public sealed class ShimmerTextBlock : TemplatedControl
{
    private readonly TextBlock textBlock = new();
    private readonly List<Visual> visibilityAncestors = [];
    private CompositionCustomVisual? animationVisual;
    private bool isAttached;
    private bool isInViewport;

    internal bool IsShimmering { get; private set; }
    internal double TextBaseline => textBlock.Padding.Top + textBlock.TextLayout.Baseline;
    internal bool IsSingleTextLine => EmoticonTextBlock.HasSingleTextLine(textBlock);

    public static readonly StyledProperty<string?> TextProperty = TextBlock.TextProperty.AddOwner<ShimmerTextBlock>();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    static ShimmerTextBlock()
    {
        AffectsMeasure<ShimmerTextBlock>(TextProperty, FontFamilyProperty, FontSizeProperty,
            FontStyleProperty, FontWeightProperty, FontStretchProperty);
        AffectsRender<ShimmerTextBlock>(ForegroundProperty);
    }

    /// <summary>创建使用当前文字颜色的扫光文本。</summary>
    public ShimmerTextBlock()
    {
        EffectiveViewportChanged += (_, args) =>
        {
            isInViewport = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateAnimation();
        };
        PropertyChanged += (_, args) =>
        {
            if (args.Property == IsVisibleProperty)
            {
                UpdateAnimation();
            }
        };
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        if (ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            animationVisual = compositor.CreateCustomVisual(new ShimmerVisualHandler());
            ElementComposition.SetElementChildVisual(this, animationVisual);
        }
        foreach (var ancestor in this.GetVisualAncestors())
        {
            visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
        UpdateAnimation();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isAttached = false;
        isInViewport = false;
        foreach (var ancestor in visibilityAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }
        visibilityAncestors.Clear();
        UpdateAnimation();
        ElementComposition.SetElementChildVisual(this, null);
        animationVisual = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        UpdateText();
        if (animationVisual is null)
        {
            textBlock.Render(context);
            return;
        }
        if (!IsShimmering)
        {
            return;
        }
        var drawing = new DrawingGroup();
        using (var recorder = drawing.Open())
        {
            textBlock.Render(recorder);
        }
        animationVisual.Size = new(Bounds.Width, Bounds.Height);
        animationVisual.SendHandlerMessage(new ShimmerTextSnapshot(drawing, Bounds.Size));
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateText();
        textBlock.Measure(availableSize);
        return textBlock.DesiredSize;
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        textBlock.Arrange(new(finalSize));
        return finalSize;
    }

    private void UpdateText()
    {
        textBlock.Text = Text;
        textBlock.FontFamily = FontFamily;
        textBlock.FontSize = FontSize;
        textBlock.FontStyle = FontStyle;
        textBlock.FontWeight = FontWeight;
        textBlock.FontStretch = FontStretch;
        textBlock.Foreground = Foreground;
    }

    private bool CanAnimate => isAttached && isInViewport && IsEffectivelyVisible;

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty)
        {
            Dispatcher.UIThread.Post(UpdateAnimation, DispatcherPriority.Loaded);
        }
    }

    private void UpdateAnimation()
    {
        var active = CanAnimate && animationVisual is not null;
        if (active == IsShimmering)
        {
            return;
        }
        IsShimmering = active;
        if (active)
        {
            InvalidateVisual();
        }
        else
        {
            animationVisual?.SendHandlerMessage(false);
        }
    }
}
