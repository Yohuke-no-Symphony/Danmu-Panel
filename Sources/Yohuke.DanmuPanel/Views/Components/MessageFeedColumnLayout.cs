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
    private double dragStartX;
    private double dragWidth;
    private double dragRatio;
    private bool isResizing;

    internal static readonly AttachedProperty<double?> IdentityRatioProperty =
        AvaloniaProperty.RegisterAttached<MessageFeedColumnLayout, Control, double?>("IdentityRatio",
            inherits: true, validate: ratio => ratio is null || double.IsFinite(ratio.Value) && ratio.Value is > 0 and < 1);

    internal static readonly RoutedEvent<MessageColumnResizeEventArgs> ResizeRequestedEvent =
        RoutedEvent.Register<MessageFeedColumnLayout, MessageColumnResizeEventArgs>("ResizeRequested", RoutingStrategies.Bubble);

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
            splitter.DragStarted += OnDragStarted;
            splitter.DragDelta += OnDragDelta;
            splitter.DragCompleted += OnDragCompleted;
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

    internal void CancelResize()
    {
        splitter?.ReleasePointer();
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

    private void OnDragStarted(object? sender, VectorEventArgs args)
    {
        args.Handled = true;
        dragStartX = splitter!.Bounds.X;
        dragWidth = ColumnDefinitions[1].ActualWidth + ColumnDefinitions[3].ActualWidth;
        dragRatio = GetRatio();
        isResizing = true;
    }

    private void OnDragDelta(object? sender, VectorEventArgs args)
    {
        args.Handled = true;
        if (!isResizing || dragWidth <= 0)
        {
            return;
        }
        var delta = splitter!.Bounds.X - dragStartX + args.Vector.X;
        var minimum = Math.Min(MIN_COLUMN_WIDTH / dragWidth, 0.49);
        var ratio = Math.Clamp(dragRatio + delta / dragWidth, minimum, 1 - minimum);
        RaiseEvent(new MessageColumnResizeEventArgs(ratio, false));
    }

    private void OnDragCompleted(object? sender, VectorEventArgs args)
    {
        args.Handled = true;
        if (isResizing)
        {
            isResizing = false;
            RaiseEvent(new MessageColumnResizeEventArgs(GetRatio(), true));
        }
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
        var minimum = Math.Min(MIN_COLUMN_WIDTH / width, 0.49);
        var delta = args.Key == Key.Right ? KEYBOARD_RATIO_STEP : -KEYBOARD_RATIO_STEP;
        RaiseEvent(new MessageColumnResizeEventArgs(Math.Clamp(GetRatio() + delta, minimum, 1 - minimum), false));
        RaiseEvent(new MessageColumnResizeEventArgs(GetRatio(), true));
    }
}
