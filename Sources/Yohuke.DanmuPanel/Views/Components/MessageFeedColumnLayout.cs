using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageFeedColumnLayout : Grid
{
    private const double MIN_COLUMN_WIDTH = 32;
    private const double KEYBOARD_RATIO_STEP = 0.02;
    private readonly double defaultRatio;
    private readonly MessageColumnSplitter? splitter;

    internal static readonly AttachedProperty<double?> IdentityRatioProperty =
        AvaloniaProperty.RegisterAttached<MessageFeedColumnLayout, Control, double?>("IdentityRatio",
            inherits: true, validate: ratio => ratio is null || double.IsFinite(ratio.Value) && ratio.Value is > 0 and < 1);

    internal static readonly RoutedEvent<MessageColumnResizeEventArgs> ResizeRequestedEvent =
        RoutedEvent.Register<MessageFeedColumnLayout, MessageColumnResizeEventArgs>("ResizeRequested", RoutingStrategies.Bubble);

    internal static readonly RoutedEvent<MessageColumnResizeStartedEventArgs> ResizeStartedEvent =
        RoutedEvent.Register<MessageFeedColumnLayout, MessageColumnResizeStartedEventArgs>("ResizeStarted", RoutingStrategies.Bubble);

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Grid);

    internal MessageFeedColumnLayout(MessageKind kind, bool canResize)
    {
        defaultRatio = kind == MessageKind.Guard ? 0.6 : 0.4;
        ColumnDefinitions = new("Auto,2*,Auto,3*");
        ColumnSpacing = 0;
        ClipToBounds = true;
        Classes.Add("danmuFeedLayout");
        if (canResize)
        {
            splitter = new();
            splitter.Bind(AutomationProperties.NameProperty, Localization.Observe("Panel.ResizeColumns"));
            splitter.Bind(ToolTip.TipProperty, Localization.Observe("Panel.ResizeColumns"));
            splitter.ResizePressed += OnResizePressed;
            splitter.KeyDown += OnSplitterKeyDown;
            SetColumn(splitter, 2);
            Children.Add(splitter);
        }
        else
        {
            var gap = new Border { MinWidth = 6 };
            gap.Classes.Add("messageColumnSplitterSpace");
            SetColumn(gap, 2);
            Children.Add(gap);
        }
        UpdateColumnWidths();
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
        if (change.Property == IdentityRatioProperty)
        {
            UpdateColumnWidths();
        }
    }

    private double GetRatio()
    {
        return GetValue(IdentityRatioProperty) ?? defaultRatio;
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
