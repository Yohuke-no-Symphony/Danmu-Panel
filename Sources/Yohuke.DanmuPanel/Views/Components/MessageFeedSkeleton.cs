using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>使用真实消息行的网格、字号和间距显示骨架，并在不可见时停止扫光。</summary>
public sealed class MessageFeedSkeleton : Button
{
    private readonly List<Visual> visibilityAncestors = [];
    private readonly List<(Control Control, double WidthRatio, double HeightRatio, HorizontalAlignment Alignment)> slots = [];
    private readonly StackPanel rows = new();
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
        IsHitTestVisible = false;
        Focusable = false;
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        ClipToBounds = true;
        Classes.Add("skeletonFeedRow");
        Template = new FuncControlTemplate<MessageFeedSkeleton>((_, _) =>
        {
            var presenter = new ContentPresenter
            {
                Name = "PART_ContentPresenter",
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            presenter.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentProperty), BindingPriority.Template);
            presenter.Bind(ContentPresenter.PaddingProperty, new TemplateBinding(PaddingProperty), BindingPriority.Template);
            presenter.Bind(ContentPresenter.HorizontalContentAlignmentProperty, new TemplateBinding(HorizontalContentAlignmentProperty), BindingPriority.Template);
            presenter.Bind(ContentPresenter.VerticalContentAlignmentProperty, new TemplateBinding(VerticalContentAlignmentProperty), BindingPriority.Template);
            return presenter;
        });
        Content = rows;
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
        var identity = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Center };
        identity.Classes.Add("danmuFeedIdentity");
        var time = CreateTextSlot("00:00:00", "danmuFeedMeta");
        DockPanel.SetDock(time, Avalonia.Controls.Dock.Right);
        identity.Children.Add(time);
        var avatar = new UserAvatarView();
        avatar.Bind(UserAvatarView.ShowAvatarProperty, new Binding("DataContext.ShowUserAvatars")
        {
            RelativeSource = new(RelativeSourceMode.FindAncestor) { AncestorType = typeof(MessageFeedSurface) },
            FallbackValue = true
        });
        Grid? medalSlot = null;
        if (Kind == MessageKind.Danmu)
        {
            var medalText = CreateTextSlot("　", "danmuMedalText");
            var medal = new Border { Child = medalText, Opacity = 0 };
            medal.Classes.Add("danmuMedalBadge");
            medalSlot = new() { Children = { medal } };
            medalSlot.Classes.Add("danmuMedalSlot");
            medalSlot.Bind(IsVisibleProperty, new Binding("DataContext.ShowFanMedals")
            {
                RelativeSource = new(RelativeSourceMode.FindAncestor) { AncestorType = typeof(MessageFeedSurface) },
                FallbackValue = true
            });
            slots.Add((medal, 1, 1, HorizontalAlignment.Left));
        }
        var name = CreateTextSlot("　", "danmuFeedUser");
        var userIdentity = new Grid
        {
            ColumnDefinitions = new("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { avatar, name }
        };
        Grid.SetColumn(name, 1);
        identity.Children.Add(userIdentity);
        slots.Add((name, 0.78, 0.65, HorizontalAlignment.Left));
        slots.Add((time, 0.9, 0.65, HorizontalAlignment.Right));
        var original = CreateTextSlot("　", "danmuFeedContent", "regularFeedContent", "originalText");
        slots.Add((original, 0.84, 0.65, HorizontalAlignment.Left));
        var compact = new HoverMarqueeTextBlock { Text = "　", Opacity = 0 };
        compact.Classes.Add("compactFeedContent");
        compact.Classes.Add("originalText");
        slots.Add((compact, 0.84, 0.65, HorizontalAlignment.Left));
        var body = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { new Grid { Children = { original, compact } } }
        };
        body.Classes.Add("messageBody");
        var layout = MessageFeedLayout.CreateGrid(Kind, canResize: false);
        if (Kind == MessageKind.SuperChat)
        {
            var header = MessageFeedLayout.CreateHeaderGrid();
            header.Children.Add(identity);
            var price = CreateTextSlot("￥100", "scFeedPrice");
            Grid.SetColumn(price, 1);
            header.Children.Add(price);
            slots.Add((price, 0.9, 0.65, HorizontalAlignment.Right));
            layout.Children.Add(header);
            Grid.SetRow(body, 1);
        }
        else
        {
            if (medalSlot is not null)
            {
                layout.Children.Add(medalSlot);
            }
            Grid.SetColumn(identity, 1);
            layout.Children.Add(identity);
            Grid.SetColumn(body, 3);
        }
        layout.Children.Add(body);
        var isCard = Kind != MessageKind.Danmu;
        var frame = new Border
        {
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Child = layout
        };
        if (isCard)
        {
            frame.Classes.Add("messageCard");
            frame.Bind(Border.BorderThicknessProperty, new DynamicResourceExtension("MessageCardBorderThickness"));
        }
        if (Kind == MessageKind.SuperChat)
        {
            frame.Classes.Add("scFeedCard");
        }
        return frame;
    }

    private static TextBlock CreateTextSlot(string text, params string[] classes)
    {
        var control = new TextBlock { Text = text, Opacity = 0, VerticalAlignment = VerticalAlignment.Center };
        foreach (var value in classes)
        {
            control.Classes.Add(value);
        }
        return control;
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
