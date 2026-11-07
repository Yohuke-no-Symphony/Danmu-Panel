using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>使用真实消息行的网格、字号和间距显示骨架，并在不可见时停止扫光。</summary>
public sealed partial class MessageFeedSkeleton : Button
{
    private readonly List<Visual> visibilityAncestors = [];
    private readonly List<(Control Control, double WidthRatio, double HeightRatio, HorizontalAlignment Alignment)> slots = [];
    private static MessageSkeletonTemplates Templates { get; } = new();
    private readonly StackPanel rows;
    private CompositionCustomVisual? animationVisual;
    private bool isAttached;
    private bool isInViewport;

    internal bool IsShimmering { get; private set; }
    internal IReadOnlyList<Rect> PlaceholderRectangles => CreateRectangles();

    public static readonly StyledProperty<int> RowCountProperty =
        AvaloniaProperty.Register<MessageFeedSkeleton, int>(nameof(RowCount), 1, validate: count => count > 0);
    public static readonly StyledProperty<MessageKind> KindProperty =
        AvaloniaProperty.Register<MessageFeedSkeleton, MessageKind>(nameof(Kind), MessageKind.Danmu);

    public int RowCount
    {
        get => GetValue(RowCountProperty);
        set => SetValue(RowCountProperty, value);
    }

    public MessageKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>创建不拦截消息列表输入的骨架行。</summary>
    public MessageFeedSkeleton()
    {
        AvaloniaXamlLoader.Load(this);
        rows = this.FindControl<StackPanel>("SkeletonRows")!;
        RebuildRows();
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
        if (change.Property == KindProperty || change.Property == RowCountProperty)
        {
            RebuildRows();
        }
        else if (change.Property == PaddingProperty || change.Property == MarginProperty)
        {
            rows.Spacing = Padding.Top + Padding.Bottom + Margin.Bottom;
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateAnimation();
        }
        if (change.Property == ForegroundProperty || change.Property == FontSizeProperty)
        {
            InvalidateVisual();
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

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Foreground is not { } foreground)
        {
            return;
        }
        var rectangles = CreateRectangles();
        var radius = FontSize * 0.25;
        using (context.PushOpacity(MessageFeedSkeletonVisualHandler.BASE_OPACITY))
        {
            foreach (var rectangle in rectangles)
            {
                context.DrawRectangle(foreground, null, rectangle, radius, radius);
            }
        }
        if (IsShimmering)
        {
            animationVisual!.Size = new(Bounds.Width, Bounds.Height);
            animationVisual.SendHandlerMessage(new MessageFeedSkeletonSnapshot(
                new(Bounds.Size), rectangles, foreground.ToImmutable(), radius));
        }
    }

    private void RebuildRows()
    {
        slots.Clear();
        rows.Children.Clear();
        Classes.Set("scFeedButton", Kind == MessageKind.SuperChat);
        Classes.Set("danmuFeedButton", Kind != MessageKind.SuperChat);
        Classes.Set("messageCardButton", Kind != MessageKind.Danmu);
        for (var index = 0; index < RowCount; index++)
        {
            rows.Children.Add(CreateRow());
        }
        InvalidateVisual();
    }

    private Control CreateRow()
    {
        var row = Templates.Build(Kind);
        foreach (var control in row.GetVisualDescendants().OfType<Control>().OrderBy(SkeletonSlot.GetOrder))
        {
            var ratio = SkeletonSlot.GetWidthRatio(control);
            if (ratio > 0)
            {
                slots.Add((control, ratio, SkeletonSlot.GetHeightRatio(control), SkeletonSlot.GetAlignment(control)));
            }
        }
        return row;
    }

    private IReadOnlyList<Rect> CreateRectangles()
    {
        var rectangles = new List<Rect>(slots.Count);
        foreach (var slot in slots)
        {
            if (!slot.Control.IsEffectivelyVisible || slot.Control.Bounds.Width <= 0 || slot.Control.Bounds.Height <= 0 ||
                slot.Control.TranslatePoint(default, this) is not { } position)
            {
                continue;
            }
            var width = slot.Control.Bounds.Width * slot.WidthRatio;
            var height = slot.Control.Bounds.Height * slot.HeightRatio;
            var left = slot.Alignment switch
            {
                HorizontalAlignment.Center => (slot.Control.Bounds.Width - width) / 2,
                HorizontalAlignment.Right => slot.Control.Bounds.Width - width,
                _ => 0
            };
            rectangles.Add(new(position.X + left, position.Y + (slot.Control.Bounds.Height - height) / 2, width, height));
        }
        return rectangles.AsReadOnly();
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty)
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
