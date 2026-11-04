using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>管理底部跟随，并在渲染前按条目身份保留阅读位置。</summary>
internal sealed class DanmuFeedScrollController : IDisposable
{
    private const double LATEST_OFFSET_THRESHOLD = 1;
    private readonly ScrollViewer scrollViewer;
    private readonly ItemsControl itemsControl;
    private readonly HashSet<INotifyPropertyChanged> observedItems = [];
    private INotifyCollectionChanged? items;
    private object? readingItem;
    private double readingY;
    private long session;
    private bool isFollowingLatest;
    private bool isFollowPending;
    private bool isAnchorCorrectionPending;
    private bool isUpdatingScroll;
    private bool isDisposed;

    public bool IsFollowingLatest => isFollowingLatest;
    public event Action<bool>? FollowingLatestChanged;

    /// <summary>连接虚拟化列表及滚动宿主，用户输入始终优先于消息更新。</summary>
    public DanmuFeedScrollController(ScrollViewer scrollViewer, ItemsControl itemsControl)
    {
        this.scrollViewer = scrollViewer;
        this.itemsControl = itemsControl;
        scrollViewer.PropertyChanged += OnScrollPropertyChanged;
        scrollViewer.LayoutUpdated += OnLayoutUpdated;
        scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged,
            RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(Gestures.ScrollGestureEvent, OnScrollGesture,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        scrollViewer.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, true);
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

    /// <summary>立即回到底部并恢复随后新消息的跟随。</summary>
    public void ScrollToLatest()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (items is not null)
        {
            ResetToLatest();
        }
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
        if (isFollowingLatest)
        {
            QueueFollowLatest();
        }
        else
        {
            QueueAnchorCorrection();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (items is null || isUpdatingScroll || isAnchorCorrectionPending)
        {
            return;
        }
        if (isFollowingLatest && !IsAtLatest())
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
            CancelPendingFollow();
            readingItem = null;
            SetFollowingLatest(IsAtLatest());
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
    }

    private void ResetToLatest()
    {
        CancelPendingFollow();
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
        QueueFollowLatest();
    }

    private void CancelPendingFollow()
    {
        session++;
        isFollowPending = false;
        isAnchorCorrectionPending = false;
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

    private void CaptureReadingAnchor()
    {
        readingItem = null;
        if (isFollowingLatest)
        {
            return;
        }
        var nearest = double.MaxValue;
        foreach (var container in itemsControl.GetRealizedContainers())
        {
            if (container.TranslatePoint(default, scrollViewer) is not { } position ||
                position.Y + container.Bounds.Height <= 0 || position.Y >= scrollViewer.Viewport.Height ||
                Math.Abs(position.Y) >= nearest)
            {
                continue;
            }
            readingItem = itemsControl.ItemFromContainer(container);
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
        if (isFollowPending)
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
                    isFollowPending = false;
                }
            }
        }, DispatcherPriority.Normal);
    }
}
