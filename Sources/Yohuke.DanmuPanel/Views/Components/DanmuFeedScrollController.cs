using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>管理底部跟随，并在渲染前按条目身份保留阅读位置。</summary>
internal sealed class DanmuFeedScrollController : IDisposable
{
    private const double LATEST_OFFSET_THRESHOLD = 1;
    private readonly ScrollViewer scrollViewer;
    private readonly ItemsControl itemsControl;
    private readonly bool followNewMessages;
    private readonly HashSet<INotifyPropertyChanged> observedItems = [];
    private INotifyCollectionChanged? items;
    private object? readingItem;
    private double readingY;
    private long session;
    private long contentRevision;
    private IPointer? scrollingPointer;
    private bool isFollowingLatest;
    private bool isFollowPending;
    private bool isAnchorCorrectionPending;
    private bool isReadingAnchorPrepared;
    private bool isUpdatingScroll;
    private bool isDisposed;

    public bool IsFollowingLatest => isFollowingLatest;
    public event Action<bool>? FollowingLatestChanged;

    /// <summary>连接虚拟化列表及滚动宿主，用户输入始终优先于消息更新。</summary>
    public DanmuFeedScrollController(ScrollViewer scrollViewer, ItemsControl itemsControl, bool followNewMessages = true)
    {
        this.scrollViewer = scrollViewer;
        this.itemsControl = itemsControl;
        this.followNewMessages = followNewMessages;
        scrollViewer.PropertyChanged += OnScrollPropertyChanged;
        scrollViewer.LayoutUpdated += OnLayoutUpdated;
        scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged,
            RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(Gestures.ScrollGestureEvent, OnScrollGesture,
            RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Direct, true);
    }

    /// <summary>切换消息集合并从底部开始阅读；空集合解除监听。</summary>
    public void Attach(INotifyCollectionChanged? collection)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (items is not null)
        {
            items.CollectionChanged -= OnCollectionChanged;
        }
        CancelPendingFollow();
        scrollingPointer = null;
        ClearItemSubscriptions();
        readingItem = null;
        items = collection;
        if (items is not null)
        {
            items.CollectionChanged += OnCollectionChanged;
            ObserveItems(items as IEnumerable);
            ResetToLatest();
        }
        else
        {
            SetFollowingLatest(false);
        }
    }

    /// <summary>立即回到底部，并按面板策略决定是否继续跟随新消息。</summary>
    public void ScrollToLatest()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (items is not null)
        {
            ResetToLatest();
        }
    }

    /// <summary>在消息移组前选择可见的邻近行，避免阅读位置追随即将移走的消息。</summary>
    public void PrepareForItemMove(object movingItem)
    {
        if (items is null || isFollowingLatest && followNewMessages)
        {
            return;
        }
        CancelPendingFollow();
        SetFollowingLatest(false);
        CaptureReadingAnchor(movingItem, requireFullyVisible: true);
        if (readingItem is null)
        {
            CaptureReadingAnchor(movingItem);
        }
        isReadingAnchorPrepared = readingItem is not null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }
        Attach(null);
        scrollViewer.PropertyChanged -= OnScrollPropertyChanged;
        scrollViewer.LayoutUpdated -= OnLayoutUpdated;
        scrollViewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
        scrollViewer.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        scrollViewer.RemoveHandler(Gestures.ScrollGestureEvent, OnScrollGesture);
        scrollViewer.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        scrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        scrollViewer.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
        isDisposed = true;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearItemSubscriptions();
            ObserveItems(items as IEnumerable);
            ResetToLatest();
        }
        else
        {
            if (e.Action != NotifyCollectionChangedAction.Move)
            {
                UnobserveItems(e.OldItems);
                ObserveItems(e.NewItems);
            }
            OnContentChanged();
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnContentChanged();
    }

    private void OnContentChanged()
    {
        contentRevision++;
        if (scrollingPointer is not null)
        {
            return;
        }
        if (isFollowingLatest && followNewMessages)
        {
            QueueFollowLatest();
        }
        else
        {
            SetFollowingLatest(false);
            QueueAnchorCorrection();
            isReadingAnchorPrepared = false;
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (items is null || isUpdatingScroll || isAnchorCorrectionPending || isReadingAnchorPrepared)
        {
            return;
        }
        if (isFollowingLatest && followNewMessages && !IsAtLatest())
        {
            QueueFollowLatest();
        }
        else
        {
            CaptureReadingAnchor();
        }
    }

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty && items is not null && !isUpdatingScroll)
        {
            if (isFollowingLatest && isFollowPending && scrollingPointer is null)
            {
                return;
            }
            CancelPendingFollow();
            readingItem = null;
            SetFollowingLatest(scrollingPointer is null && IsAtLatest());
            if (isFollowingLatest && followNewMessages)
            {
                QueueFollowLatest();
            }
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            CancelPendingFollow();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
        {
            CancelPendingFollow();
        }
    }

    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            CancelPendingFollow();
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelPendingFollow();
        if (e.Source is ScrollBar || e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>().Any())
        {
            scrollingPointer = e.Pointer;
            readingItem = null;
            SetFollowingLatest(false);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        CompletePointerScroll(e.Pointer);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CompletePointerScroll(e.Pointer);
    }

    private void CompletePointerScroll(IPointer pointer)
    {
        if (scrollingPointer != pointer)
        {
            return;
        }
        scrollingPointer = null;
        CancelPendingFollow();
        readingItem = null;
        SetFollowingLatest(IsAtLatest());
        if (isFollowingLatest && followNewMessages)
        {
            QueueFollowLatest();
        }
    }

    private void ResetToLatest()
    {
        CancelPendingFollow();
        scrollingPointer = null;
        readingItem = null;
        SetFollowingLatest(true);
        isUpdatingScroll = true;
        try
        {
            MoveToLatest();
        }
        finally
        {
            isUpdatingScroll = false;
        }
        CaptureReadingAnchor();
        QueueFollowLatest();
    }

    private void CancelPendingFollow()
    {
        session++;
        isFollowPending = false;
        isAnchorCorrectionPending = false;
        isReadingAnchorPrepared = false;
    }

    private bool IsAtLatest()
    {
        return scrollViewer.ScrollBarMaximum.Y - scrollViewer.Offset.Y <= LATEST_OFFSET_THRESHOLD;
    }

    private void MoveToLatest()
    {
        if (itemsControl.Items.Count > 0)
        {
            itemsControl.ScrollIntoView(itemsControl.Items[^1]!);
            scrollViewer.UpdateLayout();
        }
        scrollViewer.ScrollToEnd();
        scrollViewer.UpdateLayout();
    }

    private void SetFollowingLatest(bool value)
    {
        if (isFollowingLatest == value)
        {
            return;
        }
        isFollowingLatest = value;
        FollowingLatestChanged?.Invoke(value);
    }

    private void CaptureReadingAnchor(object? excludedItem = null, bool requireFullyVisible = false)
    {
        readingItem = null;
        if (isFollowingLatest && followNewMessages)
        {
            return;
        }
        var nearest = double.MaxValue;
        foreach (var container in itemsControl.GetRealizedContainers())
        {
            var item = itemsControl.ItemFromContainer(container);
            if (ReferenceEquals(item, excludedItem))
            {
                continue;
            }
            if (container.TranslatePoint(default, scrollViewer) is not { } position ||
                position.Y + container.Bounds.Height <= 0 || position.Y >= scrollViewer.Viewport.Height ||
                requireFullyVisible && (position.Y < 0 || position.Y + container.Bounds.Height > scrollViewer.Viewport.Height) ||
                Math.Abs(position.Y) >= nearest)
            {
                continue;
            }
            readingItem = item;
            readingY = position.Y;
            nearest = Math.Abs(position.Y);
        }
    }

    private void QueueAnchorCorrection()
    {
        if (isAnchorCorrectionPending || readingItem is null)
        {
            return;
        }
        isAnchorCorrectionPending = true;
        var requestedSession = session;
        var anchorItem = readingItem;
        var anchorY = readingY;
        Dispatcher.UIThread.Post(() =>
        {
            if (requestedSession != session || isDisposed || items is null || isFollowingLatest)
            {
                return;
            }
            isUpdatingScroll = true;
            try
            {
                scrollViewer.UpdateLayout();
                if (itemsControl.ContainerFromItem(anchorItem) is null && itemsControl.Items.Contains(anchorItem))
                {
                    itemsControl.ScrollIntoView(anchorItem);
                    scrollViewer.UpdateLayout();
                }
                if (itemsControl.ContainerFromItem(anchorItem) is { } anchor &&
                    anchor.TranslatePoint(default, scrollViewer) is { } position)
                {
                    var correction = position.Y - anchorY;
                    if (Math.Abs(correction) > 0.1)
                    {
                        scrollViewer.Offset = new(scrollViewer.Offset.X, scrollViewer.Offset.Y + correction);
                        scrollViewer.UpdateLayout();
                    }
                }
            }
            finally
            {
                isUpdatingScroll = false;
                if (requestedSession == session)
                {
                    isAnchorCorrectionPending = false;
                    CaptureReadingAnchor();
                }
            }
        }, DispatcherPriority.Normal);
    }

    private void ObserveItems(IEnumerable? values)
    {
        if (values is null)
        {
            return;
        }
        foreach (var value in values)
        {
            if (value is INotifyPropertyChanged item && observedItems.Add(item))
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }
        }
    }

    private void UnobserveItems(IEnumerable? values)
    {
        if (values is null)
        {
            return;
        }
        foreach (var value in values)
        {
            if (value is INotifyPropertyChanged item && observedItems.Remove(item))
            {
                item.PropertyChanged -= OnItemPropertyChanged;
            }
        }
    }

    private void ClearItemSubscriptions()
    {
        foreach (var item in observedItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
        observedItems.Clear();
    }

    private void QueueFollowLatest()
    {
        if (!isFollowingLatest || isFollowPending)
        {
            return;
        }
        isFollowPending = true;
        var requestedSession = session;
        Dispatcher.UIThread.Post(() =>
        {
            if (requestedSession != session || isDisposed || items is null || !isFollowingLatest)
            {
                return;
            }
            var followedRevision = contentRevision;
            isUpdatingScroll = true;
            try
            {
                scrollViewer.UpdateLayout();
                if (requestedSession == session && !isDisposed && items is not null && isFollowingLatest)
                {
                    MoveToLatest();
                }
            }
            finally
            {
                isUpdatingScroll = false;
                if (requestedSession == session)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (requestedSession != session || isDisposed || items is null)
                        {
                            return;
                        }
                        isFollowPending = false;
                        if (isFollowingLatest && (followedRevision != contentRevision || !IsAtLatest()))
                        {
                            QueueFollowLatest();
                        }
                    }, DispatcherPriority.Loaded);
                }
            }
        }, DispatcherPriority.Normal);
    }
}
