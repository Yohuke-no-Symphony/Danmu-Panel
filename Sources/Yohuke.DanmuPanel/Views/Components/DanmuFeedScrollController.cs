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
    private readonly bool startAtTop;
    private readonly HashSet<INotifyPropertyChanged> observedItems = [];
    private readonly HashSet<(Size Extent, Size Viewport, Rect AnchorBounds)> correctedAnchorGeometries = [];
    private readonly HashSet<(Size Extent, Size Viewport, Rect? LastBounds)> followedGeometries = [];
    private long followedGeometryRevision = -1;
    private Size followedGeometryViewport;
    private Size? followedTargetSize;
    private bool hasFollowTargetChanged;
    private INotifyCollectionChanged? items;
    private object? readingItem;
    private double readingY;
    private long session;
    private long contentRevision;
    private long lastFollowedRevision = -1;
    private Size lastFollowedViewport;
    private IPointer? scrollingPointer;
    private bool isFollowingLatest;
    private bool isFollowPending;
    private bool isAnchorCorrectionPending;
    private bool isAnchorStepQueued;
    private bool hasRequestedAnchorRealization;
    private long anchorContentRevision;
    private long userScrollRevision;
    private bool canFollowFromUserScroll;
    private bool hasUserScrollInput;
    private int viewportUpdateDepth;
    private bool isReadingAnchorPrepared;
    private bool isUpdatingScroll;
    private bool hasUserScrolledSinceReset;
    private bool isDisposed;

    public bool IsFollowingLatest => isFollowingLatest;
    public event Action<bool>? FollowingLatestChanged;

    /// <summary>连接虚拟化列表及滚动宿主，用户输入始终优先于消息更新。</summary>
    public DanmuFeedScrollController(ScrollViewer scrollViewer, ItemsControl itemsControl, bool followNewMessages = true,
        bool startAtTop = false)
    {
        this.scrollViewer = scrollViewer;
        this.itemsControl = itemsControl;
        this.followNewMessages = followNewMessages;
        this.startAtTop = startAtTop;
        scrollViewer.PropertyChanged += OnScrollPropertyChanged;
        scrollViewer.LayoutUpdated += OnLayoutUpdated;
        scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged,
            RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
        scrollViewer.AddHandler(Gestures.ScrollGestureEvent, OnScrollGesture,
            RoutingStrategies.Bubble, true);
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
            ResetPosition(startAtTop);
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
            ResetPosition(atTop: false);
            hasUserScrolledSinceReset = true;
        }
    }

    /// <summary>回到列表顶部并建立阅读锚点，取消旧的底部跟随和输入定位。</summary>
    public void ScrollToTop()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (items is null)
        {
            return;
        }
        CancelPendingFollow();
        hasUserScrolledSinceReset = true;
        userScrollRevision++;
        canFollowFromUserScroll = false;
        hasUserScrollInput = false;
        scrollingPointer = null;
        readingItem = null;
        SetFollowingLatest(false);
        MoveToTop();
        CaptureReadingAnchor();
    }

    /// <summary>定位指定的真实条目，并以新的可见位置替换旧阅读锚点。</summary>
    public void ScrollToItem(object item)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (items is null || !itemsControl.Items.Contains(item))
        {
            return;
        }
        CancelPendingFollow();
        hasUserScrolledSinceReset = true;
        userScrollRevision++;
        canFollowFromUserScroll = false;
        hasUserScrollInput = false;
        scrollingPointer = null;
        readingItem = null;
        SetFollowingLatest(false);
        isUpdatingScroll = true;
        try
        {
            itemsControl.ScrollIntoView(item);
            scrollViewer.UpdateLayout();
        }
        finally
        {
            isUpdatingScroll = false;
        }
        CaptureReadingAnchor();
    }

    private void MoveToTop()
    {
        isUpdatingScroll = true;
        try
        {
            if (itemsControl.Items.Count > 0)
            {
                itemsControl.ScrollIntoView(itemsControl.Items[0]!);
                scrollViewer.UpdateLayout();
            }
            scrollViewer.Offset = new(scrollViewer.Offset.X, 0);
            scrollViewer.UpdateLayout();
        }
        finally
        {
            isUpdatingScroll = false;
        }
    }

    internal void CompleteInitialLoad()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (items is not null && !hasUserScrolledSinceReset)
        {
            ResetPosition(startAtTop);
        }
    }

    /// <summary>跨异步消息批次固定当前阅读消息与像素位置，支持原文和拼音任务交错。</summary>
    public void BeginViewportUpdate()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (viewportUpdateDepth++ == 0 && !isFollowingLatest && !isAnchorCorrectionPending && !isReadingAnchorPrepared)
        {
            CaptureReadingAnchor();
        }
    }

    /// <summary>结束一个消息批次；最外层批次完成后结算阅读位置。</summary>
    public void EndViewportUpdate()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (viewportUpdateDepth == 0)
        {
            return;
        }
        if (--viewportUpdateDepth == 0 && !isFollowingLatest)
        {
            QueueAnchorCorrection();
        }
    }

    /// <summary>显式通知字号、换行等外部布局设置变化，并保持当前跟随或阅读策略。</summary>
    public void NotifyLayoutChanged()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        OnContentChanged();
    }

    /// <summary>在消息移组前选择可见的邻近行，避免阅读位置追随即将移走的消息。</summary>
    public void PrepareForItemMove(object movingItem)
    {
        if (items is null)
        {
            return;
        }
        hasUserScrolledSinceReset = true;
        if (isFollowingLatest && followNewMessages)
        {
            return;
        }
        CancelPendingFollow();
        SetFollowingLatest(false);
        CaptureReadingAnchor(movingItem);
        isReadingAnchorPrepared = readingItem is not null;
    }

    /// <summary>结束消息操作；未发生移组或操作失败时也释放准备锚点并恢复正常位置纠正。</summary>
    public void CompleteItemMove()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        if (isReadingAnchorPrepared)
        {
            isReadingAnchorPrepared = false;
            QueueAnchorCorrection();
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
        scrollViewer.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        scrollViewer.RemoveHandler(InputElement.PointerCaptureLostEvent, OnPointerCaptureLost);
        isDisposed = true;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Replace && readingItem is not null)
        {
            var anchorIndex = e.OldItems!.IndexOf(readingItem);
            if (anchorIndex >= 0)
            {
                readingItem = e.NewItems![anchorIndex];
                if (isAnchorCorrectionPending)
                {
                    CancelPendingFollow();
                }
            }
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ClearItemSubscriptions();
            ObserveItems(items as IEnumerable);
            ResetPosition(startAtTop);
        }
        else
        {
            if (e.Action != NotifyCollectionChangedAction.Move)
            {
                UnobserveItems(e.OldItems);
                ObserveItems(e.NewItems);
            }
            OnContentChanged(e.Action == NotifyCollectionChangedAction.Add, isCollectionChange: true);
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnContentChanged();
    }

    private void OnContentChanged(bool hasNewMessages = false, bool isCollectionChange = false)
    {
        contentRevision++;
        if (scrollingPointer is not null || isReadingAnchorPrepared && !isCollectionChange)
        {
            return;
        }
        if (isFollowingLatest && (followNewMessages || !hasNewMessages || isFollowPending))
        {
            QueueFollowLatest();
        }
        else
        {
            SetFollowingLatest(false);
            isReadingAnchorPrepared = false;
            QueueAnchorCorrection();
        }
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (items is null || isUpdatingScroll || isReadingAnchorPrepared || scrollingPointer is not null)
        {
            return;
        }
        if (isAnchorCorrectionPending)
        {
            ScheduleAnchorCorrectionStep();
            return;
        }
        if (viewportUpdateDepth > 0 && !isFollowingLatest)
        {
            if (readingItem is null)
            {
                CaptureReadingAnchor();
            }
            if (readingItem is not null && (itemsControl.ContainerFromItem(readingItem) is not { } anchor ||
                anchor.TranslatePoint(default, scrollViewer) is { } position && Math.Abs(position.Y - readingY) > 0.1))
            {
                QueueAnchorCorrection();
            }
            return;
        }
        if (isFollowingLatest)
        {
            if (!IsAtLatest() && (contentRevision != lastFollowedRevision ||
                scrollViewer.Viewport != lastFollowedViewport ||
                !followedGeometries.Contains(CaptureFollowGeometry())))
            {
                QueueFollowLatest();
            }
            return;
        }
        CaptureReadingAnchor();
    }

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.OffsetProperty && items is not null && !isUpdatingScroll)
        {
            if (isFollowingLatest && isFollowPending && scrollingPointer is null)
            {
                return;
            }
            if (isFollowingLatest && scrollingPointer is null && !hasUserScrollInput)
            {
                return;
            }
            if ((isReadingAnchorPrepared || isAnchorCorrectionPending || viewportUpdateDepth > 0) &&
                scrollingPointer is null && !hasUserScrollInput)
            {
                return;
            }
            CancelPendingFollow();
            readingItem = null;
            SetFollowingLatest(scrollingPointer is null && IsAtLatest() &&
                (isFollowingLatest || canFollowFromUserScroll));
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
            CancelFollowForUserScroll(e.Delta.Y < 0);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Up or Key.Down)
        {
            CancelFollowForUserScroll(e.Key is Key.PageDown or Key.End or Key.Down);
        }
    }

    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        if (e.Delta.Y != 0)
        {
            CancelFollowForUserScroll(e.Delta.Y > 0);
        }
    }

    private void CancelFollowForUserScroll(bool towardLatest)
    {
        hasUserScrolledSinceReset = true;
        CancelPendingFollow();
        readingItem = null;
        canFollowFromUserScroll = towardLatest;
        hasUserScrollInput = true;
        var inputRevision = ++userScrollRevision;
        if (!IsAtLatest() || !towardLatest)
        {
            SetFollowingLatest(false);
        }
        else
        {
            SetFollowingLatest(true);
            if (followNewMessages)
            {
                QueueFollowLatest();
            }
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (inputRevision == userScrollRevision)
            {
                canFollowFromUserScroll = false;
                hasUserScrollInput = false;
                if (viewportUpdateDepth > 0 && !isFollowingLatest && !isReadingAnchorPrepared && !isAnchorCorrectionPending)
                {
                    CaptureReadingAnchor();
                }
            }
        }, DispatcherPriority.Background);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        CancelPendingFollow();
        if (e.Source is ScrollBar || e.Source is Visual source && source.GetVisualAncestors().OfType<ScrollBar>().Any())
        {
            hasUserScrolledSinceReset = true;
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
        if (viewportUpdateDepth > 0 && !isFollowingLatest)
        {
            CaptureReadingAnchor();
        }
        if (isFollowingLatest && followNewMessages)
        {
            QueueFollowLatest();
        }
    }

    private void ResetPosition(bool atTop)
    {
        CancelPendingFollow();
        hasUserScrolledSinceReset = false;
        followedGeometries.Clear();
        followedTargetSize = null;
        hasFollowTargetChanged = false;
        scrollingPointer = null;
        readingItem = null;
        SetFollowingLatest(!atTop);
        if (atTop)
        {
            MoveToTop();
            CaptureReadingAnchor();
        }
        else
        {
            QueueFollowLatest();
        }
    }

    private void CancelPendingFollow()
    {
        session++;
        isFollowPending = false;
        isAnchorCorrectionPending = false;
        isAnchorStepQueued = false;
        hasRequestedAnchorRealization = false;
        correctedAnchorGeometries.Clear();
        isReadingAnchorPrepared = false;
    }

    private bool IsAtLatest()
    {
        var scaling = TopLevel.GetTopLevel(scrollViewer)?.RenderScaling ?? 1;
        var distanceInPixels = Math.Round((scrollViewer.ScrollBarMaximum.Y - scrollViewer.Offset.Y) * scaling);
        return distanceInPixels <= Math.Round(LATEST_OFFSET_THRESHOLD * scaling);
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

    private void CaptureReadingAnchor(object? excludedItem = null)
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
                position.Y >= nearest)
            {
                continue;
            }
            readingItem = item;
            readingY = position.Y;
            nearest = position.Y;
        }
    }

    private void QueueAnchorCorrection()
    {
        if (isAnchorCorrectionPending || readingItem is null)
        {
            return;
        }
        isAnchorCorrectionPending = true;
        hasRequestedAnchorRealization = false;
        correctedAnchorGeometries.Clear();
        anchorContentRevision = contentRevision;
        ScheduleAnchorCorrectionStep();
    }

    private void ScheduleAnchorCorrectionStep()
    {
        if (isAnchorStepQueued || !isAnchorCorrectionPending)
        {
            return;
        }
        isAnchorStepQueued = true;
        var requestedSession = session;
        Dispatcher.UIThread.Post(() =>
        {
            if (requestedSession != session)
            {
                return;
            }
            isAnchorStepQueued = false;
            AdvanceAnchorCorrection();
        }, DispatcherPriority.Background);
    }

    private void AdvanceAnchorCorrection()
    {
        if (isDisposed || items is null || !isAnchorCorrectionPending || isFollowingLatest)
        {
            return;
        }
        if (!scrollViewer.IsArrangeValid || !itemsControl.IsArrangeValid ||
            itemsControl.ItemsPanelRoot is { IsMeasureValid: false } or { IsArrangeValid: false })
        {
            return;
        }
        var anchorItem = readingItem;
        if (anchorItem is null || !itemsControl.Items.Contains(anchorItem))
        {
            CompleteAnchorCorrection();
            return;
        }
        if (itemsControl.ContainerFromItem(anchorItem) is not { } anchor)
        {
            if (!hasRequestedAnchorRealization)
            {
                hasRequestedAnchorRealization = true;
                isUpdatingScroll = true;
                try
                {
                    itemsControl.ScrollIntoView(anchorItem);
                }
                finally
                {
                    isUpdatingScroll = false;
                }
                ScheduleAnchorCorrectionStep();
            }
            else
            {
                CompleteAnchorCorrection();
            }
            return;
        }
        if (anchor.TranslatePoint(default, scrollViewer) is { } position &&
            Math.Abs(position.Y - readingY) > 0.1 &&
            correctedAnchorGeometries.Add((scrollViewer.Extent, scrollViewer.Viewport, anchor.Bounds)))
        {
            isUpdatingScroll = true;
            try
            {
                scrollViewer.Offset = new(scrollViewer.Offset.X, scrollViewer.Offset.Y + position.Y - readingY);
            }
            finally
            {
                isUpdatingScroll = false;
            }
            ScheduleAnchorCorrectionStep();
            return;
        }
        CompleteAnchorCorrection();
    }

    private void CompleteAnchorCorrection()
    {
        var changedDuringCorrection = anchorContentRevision != contentRevision;
        isAnchorCorrectionPending = false;
        if (changedDuringCorrection && readingItem is not null && itemsControl.Items.Contains(readingItem))
        {
            QueueAnchorCorrection();
        }
        else if (viewportUpdateDepth == 0)
        {
            CaptureReadingAnchor();
        }
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
        if (followedGeometryRevision != contentRevision || followedGeometryViewport != scrollViewer.Viewport)
        {
            followedGeometryRevision = contentRevision;
            followedGeometryViewport = scrollViewer.Viewport;
            followedGeometries.Clear();
            followedTargetSize = null;
            hasFollowTargetChanged = false;
        }
        if (hasFollowTargetChanged)
        {
            return;
        }
        var geometry = CaptureFollowGeometry();
        if (followedTargetSize is { } targetSize &&
            (geometry.LastBounds is not { } lastBounds || lastBounds.Size != targetSize))
        {
            return;
        }
        if (!followedGeometries.Add(geometry))
        {
            return;
        }
        isFollowPending = true;
        var requestedSession = session;
        Dispatcher.UIThread.Post(() => FollowLatest(requestedSession), DispatcherPriority.Normal);
    }

    private void FollowLatest(long requestedSession)
    {
        if (!IsCurrentFollowSession(requestedSession) || !isFollowingLatest)
        {
            if (requestedSession == session)
            {
                isFollowPending = false;
            }
            return;
        }
        var followedRevision = contentRevision;
        (Size Extent, Size Viewport, Rect? LastBounds) followedGeometry = default;
        isUpdatingScroll = true;
        try
        {
            scrollViewer.UpdateLayout();
            if (IsCurrentFollowSession(requestedSession) && isFollowingLatest)
            {
                MoveToLatest();
                followedGeometry = CaptureFollowGeometry();
                RecordFollowTargetSize(followedGeometry.LastBounds?.Size);
            }
        }
        finally
        {
            isUpdatingScroll = false;
            if (requestedSession == session)
            {
                Dispatcher.UIThread.Post(() => SettleFollowLatest(requestedSession, followedRevision, followedGeometry),
                    DispatcherPriority.Loaded);
            }
        }
    }

    private void SettleFollowLatest(long requestedSession, long followedRevision,
        (Size Extent, Size Viewport, Rect? LastBounds) followedGeometry)
    {
        if (!IsCurrentFollowSession(requestedSession))
        {
            return;
        }
        var settledRevision = contentRevision;
        isUpdatingScroll = true;
        try
        {
            if (isFollowingLatest && (followedRevision != contentRevision || !IsAtLatest() ||
                CaptureFollowGeometry() != followedGeometry))
            {
                var geometry = CaptureFollowGeometry();
                followedGeometries.Add(geometry);
                RecordFollowTargetSize(geometry.LastBounds?.Size);
                MoveToLatest();
                RecordFollowTargetSize(CaptureFollowGeometry().LastBounds?.Size);
            }
        }
        finally
        {
            isUpdatingScroll = false;
            if (requestedSession == session)
            {
                lastFollowedRevision = settledRevision;
                lastFollowedViewport = scrollViewer.Viewport;
                Dispatcher.UIThread.Post(() => CompleteFollowLatest(requestedSession), DispatcherPriority.Input);
            }
        }
    }

    private void CompleteFollowLatest(long requestedSession)
    {
        if (!IsCurrentFollowSession(requestedSession))
        {
            return;
        }
        isFollowPending = false;
        if (isFollowingLatest && (lastFollowedRevision != contentRevision ||
            lastFollowedViewport != scrollViewer.Viewport || !IsAtLatest() &&
            !followedGeometries.Contains(CaptureFollowGeometry())))
        {
            QueueFollowLatest();
        }
        else
        {
            CaptureReadingAnchor();
        }
    }

    private void RecordFollowTargetSize(Size? size)
    {
        if (followedTargetSize is { } targetSize && size is { } actualSize && targetSize != actualSize)
        {
            hasFollowTargetChanged = true;
        }
        followedTargetSize ??= size;
    }

    private bool IsCurrentFollowSession(long requestedSession)
    {
        return requestedSession == session && !isDisposed && items is not null;
    }

    private (Size Extent, Size Viewport, Rect? LastBounds) CaptureFollowGeometry()
    {
        var lastIndex = itemsControl.Items.Count - 1;
        return (scrollViewer.Extent, scrollViewer.Viewport,
            lastIndex >= 0 ? itemsControl.ContainerFromIndex(lastIndex)?.Bounds : null);
    }
}
