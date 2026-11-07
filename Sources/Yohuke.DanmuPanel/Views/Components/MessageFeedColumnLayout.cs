using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private double GetRatio()
    {
        return GetValue(IdentityRatioProperty) ?? (Kind == MessageKind.Guard ? 0.6 : 0.4);
    }

    private void UpdateColumnWidths()
    {
        var ratio = GetRatio();
        ColumnDefinitions[1].Width = new(ratio, GridUnitType.Star);
        ColumnDefinitions[3].Width = new(1 - ratio, GridUnitType.Star);
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs args)
    {
        args.Handled = true;
        var width = ColumnDefinitions[1].ActualWidth + ColumnDefinitions[3].ActualWidth;
        RaiseEvent(new MessageColumnResizeStartedEventArgs(args.Pointer,
            args.GetPosition(TopLevel.GetTopLevel(this)), width, GetRatio()));
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
        RaiseEvent(new MessageColumnResizeEventArgs(ClampRatio(GetRatio() + delta, width), false));
        RaiseEvent(new MessageColumnResizeEventArgs(GetRatio(), true));
    }
}
