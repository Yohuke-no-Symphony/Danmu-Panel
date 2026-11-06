using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class MessageColumnResizeController
{
    private readonly Control host;
    private readonly Action<double, bool> applyResize;
    private IPointer? pointer;
    private TopLevel? coordinateRoot;
    private Point startPosition;
    private double startRatio;
    private double width;
    private double lastRatio;

    internal MessageColumnResizeController(Control host, Action<double, bool> applyResize)
    {
        this.host = host;
        this.applyResize = applyResize;
        host.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, true);
        host.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, true);
        host.PointerCaptureLost += OnPointerCaptureLost;
    }

    internal void Begin(MessageColumnResizeStartedEventArgs args)
    {
        Cancel();
        if (args.Width <= 0)
        {
            return;
        }
        coordinateRoot = TopLevel.GetTopLevel(host)!;
        startPosition = args.RootPosition;
        width = args.Width;
        startRatio = args.Ratio;
        lastRatio = startRatio;
        pointer = args.Pointer;
        pointer.Capture(host);
    }

    internal void Cancel()
    {
        var active = pointer;
        if (active is null)
        {
            return;
        }
        pointer = null;
        coordinateRoot = null;
        if (ReferenceEquals(active.Captured, host))
        {
            active.Capture(null);
        }
        applyResize(lastRatio, true);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs args)
    {
        if (!ReferenceEquals(args.Pointer, pointer))
        {
            return;
        }
        args.Handled = true;
        var delta = args.GetPosition(coordinateRoot!).X - startPosition.X;
        lastRatio = MessageFeedColumnLayout.ClampRatio(startRatio + delta / width, width);
        applyResize(lastRatio, false);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (ReferenceEquals(args.Pointer, pointer) && args.InitialPressMouseButton == MouseButton.Left)
        {
            args.Handled = true;
            Cancel();
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs args)
    {
        if (ReferenceEquals(args.Pointer, pointer) && !ReferenceEquals(args.Pointer.Captured, host))
        {
            Cancel();
        }
    }
}
