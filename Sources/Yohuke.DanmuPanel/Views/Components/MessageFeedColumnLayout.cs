using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class MessageFeedColumnLayout : Grid
{
    private const double MIN_COLUMN_WIDTH = 32;
    private const double KEYBOARD_RATIO_STEP = 0.02;
    private readonly MessageColumnSplitter splitter;

    public static readonly StyledProperty<MessageKind> KindProperty =
        AvaloniaProperty.Register<MessageFeedColumnLayout, MessageKind>(nameof(Kind));
    public static readonly StyledProperty<bool> CanResizeProperty =
        AvaloniaProperty.Register<MessageFeedColumnLayout, bool>(nameof(CanResize), true);

    public MessageKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool CanResize
    {
        get => GetValue(CanResizeProperty);
        set => SetValue(CanResizeProperty, value);
    }

    internal static readonly AttachedProperty<double?> IdentityRatioProperty =
        AvaloniaProperty.RegisterAttached<MessageFeedColumnLayout, Control, double?>("IdentityRatio",
            inherits: true, validate: ratio => ratio is null || double.IsFinite(ratio.Value) && ratio.Value is > 0 and < 1);

    internal static readonly RoutedEvent<MessageColumnResizeEventArgs> ResizeRequestedEvent =
        RoutedEvent.Register<MessageFeedColumnLayout, MessageColumnResizeEventArgs>("ResizeRequested", RoutingStrategies.Bubble);

    internal static readonly RoutedEvent<MessageColumnResizeStartedEventArgs> ResizeStartedEvent =
        RoutedEvent.Register<MessageFeedColumnLayout, MessageColumnResizeStartedEventArgs>("ResizeStarted", RoutingStrategies.Bubble);

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Grid);

    public MessageFeedColumnLayout()
    {
        AvaloniaXamlLoader.Load(this);
        splitter = this.FindControl<MessageColumnSplitter>("ColumnSplitter")!;
        splitter.ResizePressed += OnResizePressed;
        splitter.KeyDown += OnSplitterKeyDown;
        UpdateColumnWidths();
    }

    internal MessageFeedColumnLayout(MessageKind kind, bool canResize)
        : this()
    {
        Kind = kind;
        CanResize = canResize;
    }

    internal static double ClampRatio(double ratio, double width)
    {
        var minimum = Math.Min(MIN_COLUMN_WIDTH / width, 0.49);
        return Math.Clamp(ratio, minimum, 1 - minimum);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IdentityRatioProperty || change.Property == KindProperty)
        {
            UpdateColumnWidths();
        }
    }

    /// <summary>按共享比例确定正文与身份列的像素宽度，避免内容测量取整改变分栏位置。</summary>
    /// <remarks>身份列保留 Star，使跨粉丝牌与身份列的摘要内容接受有限宽度测量。</remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsPositiveInfinity(availableSize.Width))
        {
            UpdateColumnWidths();
            return base.MeasureOverride(availableSize);
        }

        var medalWidth = 0d;
        var splitterWidth = 0d;
        foreach (var child in Children)
        {
            var column = GetColumn(child);
            if (GetColumnSpan(child) != 1 || column is not (0 or 2))
            {
                continue;
            }
            child.Measure(new(double.PositiveInfinity, availableSize.Height));
            if (column == 0)
            {
                medalWidth = Math.Max(medalWidth, child.DesiredSize.Width);
            }
            else
            {
                splitterWidth = Math.Max(splitterWidth, child.DesiredSize.Width);
            }
        }

        var scale = LayoutHelper.GetLayoutScale(this);
        var availableWidth = UseLayoutRounding
            ? LayoutHelper.RoundLayoutValueUp(availableSize.Width, scale) : availableSize.Width;
        var remainingWidth = Math.Max(0,
            availableWidth - medalWidth - splitterWidth - ColumnSpacing * (ColumnDefinitions.Count - 1));
        var identityWidth = remainingWidth * GetRatio(this, Kind);
        if (UseLayoutRounding)
        {
            identityWidth = LayoutHelper.RoundLayoutValue(identityWidth, scale);
        }
        identityWidth = Math.Clamp(identityWidth, 0, remainingWidth);
        ColumnDefinitions[1].Width = new(1, GridUnitType.Star);
        ColumnDefinitions[3].Width = new(Math.Max(0, remainingWidth - identityWidth), GridUnitType.Pixel);
        return base.MeasureOverride(availableSize);
    }

    internal static double GetRatio(Control control, MessageKind kind)
    {
        return control.GetValue(IdentityRatioProperty) ?? (kind == MessageKind.Guard ? 0.6 : 0.4);
    }

    private void UpdateColumnWidths()
    {
        ApplyColumnWidths(this, GetRatio(this, Kind));
    }

    internal static void ApplyColumnWidths(Grid layout, double ratio)
    {
        layout.ColumnDefinitions[1].Width = new(ratio, GridUnitType.Star);
        layout.ColumnDefinitions[3].Width = new(1 - ratio, GridUnitType.Star);
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs args)
    {
        args.Handled = true;
        var width = ColumnDefinitions[1].ActualWidth + ColumnDefinitions[3].ActualWidth;
        RaiseEvent(new MessageColumnResizeStartedEventArgs(args.Pointer,
            args.GetPosition(TopLevel.GetTopLevel(this)), width, GetRatio(this, Kind)));
    }

    private void OnSplitterKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key is not (Key.Left or Key.Right))
        {
            return;
        }
        args.Handled = true;
        var width = ColumnDefinitions[1].ActualWidth + ColumnDefinitions[3].ActualWidth;
        if (width <= 0)
        {
            return;
        }
        var delta = args.Key == Key.Right ? KEYBOARD_RATIO_STEP : -KEYBOARD_RATIO_STEP;
        RaiseEvent(new MessageColumnResizeEventArgs(ClampRatio(GetRatio(this, Kind) + delta, width), false));
        RaiseEvent(new MessageColumnResizeEventArgs(GetRatio(this, Kind), true));
    }
}
