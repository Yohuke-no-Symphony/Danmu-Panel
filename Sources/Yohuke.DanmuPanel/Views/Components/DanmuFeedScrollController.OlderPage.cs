using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class DanmuFeedScrollController
{
    private const double OLDER_EDGE_PHYSICAL_PIXELS = 1;
    private const int WHEEL_BURST_IDLE_MILLISECONDS = 150;
    private readonly Action? requestOlderPage;
    private PointerWheelEventArgs? olderWheelInput;
    private long lastVerticalWheelAt;
    private bool wheelRequestedOlderPage;
    private KeyEventArgs? olderKeyInput;
    private Key? olderKey;
    private bool keyRequestedOlderPage;
    private int? scrollGestureId;
    private int? cancelledScrollGestureId;
    private bool gestureRequestedOlderPage;
    private ScrollBar? olderScrollbar;
    private IPointer? olderScrollbarPointer;
    private Thumb? olderScrollbarThumb;
    private double olderScrollbarPointerY;
    private bool scrollbarRequestedOlderPage;
    private long olderPageRevision;
    private bool olderPagePending;
    private bool olderPageCheckPosted;

    internal void CancelOlderPageInput()
    {
        CancelQueuedOlderPage();
        olderWheelInput = null;
        wheelRequestedOlderPage = lastVerticalWheelAt != 0;
        olderKeyInput = null;
        keyRequestedOlderPage = olderKey is not null;
        cancelledScrollGestureId = scrollGestureId;
        gestureRequestedOlderPage = scrollGestureId is not null;
        EndOlderScrollbarInput();
    }

    private void ObserveOlderPageInput()
    {
        scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnOlderWheelCompleted, RoutingStrategies.Bubble, true);
        itemsControl.AddHandler(InputElement.KeyDownEvent, OnOlderKeyCompleted, RoutingStrategies.Bubble, true);
        itemsControl.AddHandler(InputElement.KeyUpEvent, OnOlderKeyReleased, RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.PointerMovedEvent, OnOlderScrollbarMoved, RoutingStrategies.Bubble, true);
        scrollViewer.AddHandler(InputElement.PointerReleasedEvent, OnOlderScrollbarReleased, RoutingStrategies.Bubble, true);
        scrollViewer.AddHandler(Thumb.DragCompletedEvent, OnOlderThumbCompleted, RoutingStrategies.Bubble, true);
    }

    private void StopObservingOlderPageInput()
    {
        scrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnOlderWheelCompleted);
        itemsControl.RemoveHandler(InputElement.KeyDownEvent, OnOlderKeyCompleted);
        itemsControl.RemoveHandler(InputElement.KeyUpEvent, OnOlderKeyReleased);
        scrollViewer.RemoveHandler(InputElement.PointerMovedEvent, OnOlderScrollbarMoved);
        scrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnOlderScrollbarReleased);
        scrollViewer.RemoveHandler(Thumb.DragCompletedEvent, OnOlderThumbCompleted);
    }

    private bool TrackOlderWheel(PointerWheelEventArgs args)
    {
        if (requestOlderPage is null || !IsVerticalInput(args.Delta.X, args.Delta.Y) || !IsOwnedScrollSource(args.Source))
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        if (args.Delta.Y < 0 || Stopwatch.GetElapsedTime(lastVerticalWheelAt, now).TotalMilliseconds >= WHEEL_BURST_IDLE_MILLISECONDS)
        {
            wheelRequestedOlderPage = false;
        }
        lastVerticalWheelAt = now;
        if (args.Delta.Y < 0)
        {
            CancelQueuedOlderPage();
            olderWheelInput = null;
        }
        else if (!wheelRequestedOlderPage)
        {
            olderWheelInput = args;
        }

        return wheelRequestedOlderPage && IsAtOlderEdge();
    }

    private void OnOlderWheelCompleted(object? sender, PointerWheelEventArgs args)
    {
        if (ReferenceEquals(olderWheelInput, args))
        {
            olderWheelInput = null;
            wheelRequestedOlderPage = TryQueueOlderPage();
        }
    }

    private bool TrackOlderKey(KeyEventArgs args)
    {
        if (requestOlderPage is null || !IsOwnedScrollSource(args.Source))
        {
            return false;
        }

        if (args.Key is not (Key.Up or Key.PageUp or Key.Home))
        {
            CancelQueuedOlderPage();
            return false;
        }
        if (olderKey != args.Key)
        {
            olderKey = args.Key;
            keyRequestedOlderPage = false;
        }
        if (!keyRequestedOlderPage)
        {
            olderKeyInput = args;
        }

        return keyRequestedOlderPage && IsAtOlderEdge();
    }

    private void OnOlderKeyCompleted(object? sender, KeyEventArgs args)
    {
        if (ReferenceEquals(olderKeyInput, args))
        {
            olderKeyInput = null;
            keyRequestedOlderPage = TryQueueOlderPage();
        }
    }

    private void OnOlderKeyReleased(object? sender, KeyEventArgs args)
    {
        if (olderKey == args.Key)
        {
            olderKey = null;
            keyRequestedOlderPage = false;
        }
    }

    private bool TrackOlderGesture(ScrollGestureEventArgs args)
    {
        if (requestOlderPage is null || !IsVerticalInput(args.Delta.X, args.Delta.Y) || !IsOwnedScrollSource(args.Source))
        {
            return false;
        }

        if (args.Id == cancelledScrollGestureId)
        {
            return args.Delta.Y < 0 && IsAtOlderEdge();
        }
        if (scrollGestureId != args.Id)
        {
            scrollGestureId = args.Id;
            gestureRequestedOlderPage = false;
        }
        if (args.Delta.Y > 0)
        {
            CancelQueuedOlderPage();
            return false;
        }
        var keepAnchor = gestureRequestedOlderPage && IsAtOlderEdge();
        if (!gestureRequestedOlderPage)
        {
            gestureRequestedOlderPage = TryQueueOlderPage();
        }
        return keepAnchor;
    }

    private void TrackOlderScrollbarPress(ScrollBar bar, PointerPressedEventArgs args)
    {
        if (requestOlderPage is null || bar.Orientation != Orientation.Vertical || !IsOwnedScrollSource(bar) ||
            !args.GetCurrentPoint(bar).Properties.IsLeftButtonPressed)
        {
            return;
        }

        EndOlderScrollbarInput();
        olderScrollbar = bar;
        olderScrollbarPointer = args.Pointer;
        olderScrollbarThumb = args.Source as Thumb ??
            (args.Source as Visual)?.GetVisualAncestors().OfType<Thumb>().FirstOrDefault();
        olderScrollbarPointerY = args.GetPosition(scrollViewer).Y;
        scrollbarRequestedOlderPage = false;
        bar.Scroll += OnOlderScrollbarScrolled;
    }

    private void OnOlderScrollbarScrolled(object? sender, ScrollEventArgs args)
    {
        if (!scrollbarRequestedOlderPage && args.ScrollEventType is ScrollEventType.SmallDecrement or ScrollEventType.LargeDecrement)
        {
            scrollbarRequestedOlderPage = TryQueueOlderPage();
        }
    }

    private void OnOlderScrollbarMoved(object? sender, PointerEventArgs args)
    {
        if (olderScrollbarThumb is null || olderScrollbarPointer != args.Pointer)
        {
            return;
        }

        var y = args.GetPosition(scrollViewer).Y;
        var delta = y - olderScrollbarPointerY;
        olderScrollbarPointerY = y;
        if (delta > 0)
        {
            CancelQueuedOlderPage();
        }
        else if (delta < 0 && !scrollbarRequestedOlderPage)
        {
            scrollbarRequestedOlderPage = TryQueueOlderPage();
        }
    }

    private void OnOlderScrollbarReleased(object? sender, PointerReleasedEventArgs args)
    {
        FinishOlderScrollbarInput(args.Pointer);
    }

    private void OnOlderThumbCompleted(object? sender, VectorEventArgs args)
    {
        if (ReferenceEquals(args.Source, olderScrollbarThumb) && olderScrollbarPointer is { } pointer)
        {
            CompletePointerScroll(pointer);
            FinishOlderScrollbarInput(pointer);
        }
    }

    private void FinishOlderScrollbarInput(IPointer pointer)
    {
        if (olderScrollbarPointer == pointer)
        {
            EndOlderScrollbarInput();
            ScheduleOlderPageCheck();
        }
    }

    private void EndOlderScrollbarInput()
    {
        if (olderScrollbar is { } bar)
        {
            bar.Scroll -= OnOlderScrollbarScrolled;
        }
        olderScrollbar = null;
        olderScrollbarPointer = null;
        olderScrollbarThumb = null;
    }

    private static bool IsVerticalInput(double x, double y)
    {
        return y != 0 && Math.Abs(y) >= Math.Abs(x);
    }

    private bool IsOwnedScrollSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, scrollViewer) || ReferenceEquals(visual, itemsControl))
            {
                return true;
            }
            if (visual is ScrollViewer)
            {
                return false;
            }
        }
        return false;
    }

    private bool IsAtOlderEdge()
    {
        var scaling = TopLevel.GetTopLevel(scrollViewer)?.RenderScaling ?? 1;
        return scrollViewer.Offset.Y * scaling <= OLDER_EDGE_PHYSICAL_PIXELS;
    }

    private bool TryQueueOlderPage()
    {
        if (items is null || itemsControl.ItemCount == 0 || isUpdatingScroll ||
            scrollViewer.Viewport.Height <= 0 || !IsAtOlderEdge())
        {
            return false;
        }

        olderPagePending = true;
        ScheduleOlderPageCheck();
        return true;
    }

    private void CancelOlderRequestIfAwayFromEdge()
    {
        if (olderPagePending && !IsAtOlderEdge())
        {
            CancelQueuedOlderPage();
        }
    }

    private void CancelQueuedOlderPage()
    {
        if (olderPagePending)
        {
            olderPageRevision++;
            olderPagePending = false;
            olderPageCheckPosted = false;
        }
    }

    private void ScheduleOlderPageCheck()
    {
        if (!olderPagePending || olderPageCheckPosted || scrollingPointer is not null)
        {
            return;
        }

        olderPageCheckPosted = true;
        var version = olderPageRevision;
        Dispatcher.UIThread.Post(() => SubmitOlderPage(version), DispatcherPriority.Loaded);
    }

    private void SubmitOlderPage(long version)
    {
        if (version != olderPageRevision)
        {
            return;
        }

        olderPageCheckPosted = false;
        if (!olderPagePending || scrollingPointer is not null)
        {
            return;
        }
        if (isDisposed || items is null || !IsAtOlderEdge())
        {
            CancelQueuedOlderPage();
            return;
        }
        if (!scrollViewer.IsArrangeValid || !itemsControl.IsArrangeValid ||
            itemsControl.ItemsPanelRoot is { IsMeasureValid: false } or { IsArrangeValid: false })
        {
            return;
        }

        olderPagePending = false;
        CancelPendingFollow();
        userScrollRevision++;
        hasUserScrollInput = false;
        canFollowFromUserScroll = false;
        SetFollowingLatest(false);
        CaptureReadingAnchor(realMessagesOnly: true);
        if (readingItem is null)
        {
            return;
        }

        isReadingAnchorPrepared = true;
        try
        {
            requestOlderPage?.Invoke();
        }
        finally
        {
            isReadingAnchorPrepared = false;
        }
    }
}
