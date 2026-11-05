using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>在待生成拼音的位置显示无文字短矩形，隐藏和离开视口后停止扫光。</summary>
public sealed class PinyinLoadingPlaceholder : TemplatedControl
{
    private readonly List<Visual> visibilityAncestors = [];
    private CompositionCustomVisual? animationVisual;
    private bool isAttached;
    private bool isInViewport;

    internal bool IsShimmering { get; private set; }

    public static readonly StyledProperty<double> LengthProperty =
        AvaloniaProperty.Register<PinyinLoadingPlaceholder, double>(nameof(Length), 6,
            validate: length => double.IsFinite(length) && length > 0);

    public double Length
    {
        get => GetValue(LengthProperty);
        set => SetValue(LengthProperty, value);
    }

    static PinyinLoadingPlaceholder()
    {
        AffectsMeasure<PinyinLoadingPlaceholder>(FontSizeProperty, LengthProperty);
        AffectsRender<PinyinLoadingPlaceholder>(FontSizeProperty, ForegroundProperty);
    }

    /// <summary>创建不拦截原文点击和滚动输入的拼音占位。</summary>
    public PinyinLoadingPlaceholder()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        ClipToBounds = true;
        EffectiveViewportChanged += (_, args) =>
        {
            isInViewport = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateAnimation();
        };
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateAnimation();
        }
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        return new(Math.Min(availableSize.Width, FontSize * Length), FontSize * 1.3);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Foreground is not { } foreground)
        {
            return;
        }
        var height = Math.Min(Bounds.Height, FontSize * 0.65);
        var rectangle = new Rect(0, (Bounds.Height - height) / 2, Bounds.Width, height);
        var radius = FontSize * 0.2;
        using (context.PushOpacity(MessageFeedSkeletonVisualHandler.BASE_OPACITY))
        {
            context.DrawRectangle(foreground, null, rectangle, radius, radius);
        }
        if (IsShimmering)
        {
            animationVisual!.Size = new(Bounds.Width, Bounds.Height);
            animationVisual.SendHandlerMessage(new MessageFeedSkeletonSnapshot(new(Bounds.Size),
                new[] { rectangle }, foreground.ToImmutable(), radius));
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        if (ElementComposition.GetElementVisual(this)?.Compositor is { } compositor)
        {
            animationVisual = compositor.CreateCustomVisual(new MessageFeedSkeletonVisualHandler());
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

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty)
        {
            Dispatcher.UIThread.Post(UpdateAnimation, DispatcherPriority.Loaded);
        }
    }

    private void UpdateAnimation()
    {
        var active = isAttached && isInViewport && IsEffectivelyVisible && animationVisual is not null;
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
