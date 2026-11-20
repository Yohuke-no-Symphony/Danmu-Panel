using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>分页加载并原位刷新消息列表。</summary>
public sealed partial class FeedViewModel : ObservableObject, IDisposable
{
    private const int HISTORY_PAGE_SIZE = 100;
    private const int BUFFERED_HISTORY_PAGES = 3;
    private const int LOADING_HINT_DELAY_MILLISECONDS = 150;
    private static readonly TimeSpan LIVE_REFRESH_INTERVAL = TimeSpan.FromSeconds(1.0 / 60);
    private static readonly TimeSpan SNAPSHOT_REFRESH_INTERVAL = TimeSpan.FromMilliseconds(150);
    private readonly PanelRuntime runtime;
    private readonly MessageKind kind;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly DispatcherTimer refreshTimer;
    private CancellationTokenSource lifetime = new();
    private RoomTabState? room;
    private int retentionCount;
    private MessageFeedPage? scrollingPage;
    private MessageFeedPage? pinnedPage;
    private MessageFeedPageRequest? retainedHistory;
    private MessageFeedPageRequest? retainedPins;
    private bool keepHistoryWindow;
    private bool keepPinnedWindow;
    private bool showNewest;
    private int revision;
    private int unreadCount;
    private int refreshRequested;
    private int refreshScheduled;
    private int loadingRevision;
    private bool isLoadingHistory;
    private bool hasLoadedRoom;
    private Task? historyLoadTask;
    private CancellationTokenSource? historyPageRequest;
    private bool disposed;
    private bool hasPinnedItems;
    private bool hasScrollingItems;
    private bool isCompactDisplay;
    private bool isCompactMessageWrap;
    private bool isCompactSuperChatWrap;
    private bool showUserAvatars;
    private bool showFanMedals;
    private double messageTextScale = 1;
    private MessageDisplaySettings? displaySettings;
    private readonly HashSet<string> translationRequested = new();
    public ObservableCollection<MessageItemViewModel> Items { get; } = new FeedCollection<MessageItemViewModel>();
    public ObservableCollection<MessageItemViewModel> PinnedItems { get; } = new FeedCollection<MessageItemViewModel>();
    public ObservableCollection<MessageItemViewModel> ScrollingItems { get; } = new FeedCollection<MessageItemViewModel>();
    public ObservableCollection<FeedRowViewModel> ScrollingRows { get; } = new FeedCollection<FeedRowViewModel>();
    public bool HasPinnedItems => hasPinnedItems;
    public bool HasScrollingItems => hasScrollingItems;
    public int UnreadCount
    {
        get => unreadCount;
        private set
        {
            if (SetProperty(ref unreadCount, value))
            {
                OnPropertyChanged(nameof(HasUnreadMessages));
            }
        }
    }
    public bool HasUnreadMessages => UnreadCount > 0;
    public RoomTabState? SelectedRoom => room;
    public bool HasSelectedRoom => room is not null;
    public bool IsReadingHistory { get; set; }
    public bool HasOlderHistory => scrollingPage?.HasOlder == true;
    public bool HasNewerHistory => scrollingPage?.HasNewer == true;
    public bool HasOlderPinnedMessages => pinnedPage?.HasOlder == true;
    public bool HasNewerPinnedMessages => pinnedPage?.HasNewer == true;
    public bool IsLoadingHistory
    {
        get => isLoadingHistory;
        private set => SetProperty(ref isLoadingHistory, value);
    }
    public event Action<Exception>? Failed;
    public event Action? InitialLoadCompleted;
    /// <summary>通知视图开始可能跨多个界面帧的消息布局更新。</summary>
    public event Action? ViewportUpdateStarted;
    /// <summary>通知视图结束一轮消息布局更新。</summary>
    public event Action? ViewportUpdateCompleted;
    internal int ViewportUpdateDepth { get; private set; }
    internal int WindowRevision => revision;
    internal Func<MessageItemViewModel?>? ReadVisibleAnchor { get; set; }

    public bool IsCompactDisplay => isCompactDisplay;
    public bool IsCompactMessageWrap => isCompactMessageWrap;
    public bool IsCompactSuperChatWrap => isCompactSuperChatWrap;
    public double MessageTextScale => messageTextScale;
    /// <summary>历史加载占位与真实消息共用的头像显示偏好。</summary>
    public bool ShowUserAvatars => showUserAvatars;
    /// <summary>历史加载占位与真实弹幕共用的粉丝牌显示偏好。</summary>
    public bool ShowFanMedals => showFanMedals;

    /// <summary>连接一个独立消息类型的列表。</summary>
    public FeedViewModel(PanelRuntime runtime, MessageKind kind)
    {
        this.runtime = runtime;
        this.kind = kind;
        readFilter = kind == MessageKind.Danmu ? MessageReadFilter.All : MessageReadFilter.Unread;
        UpdateSortOrder();
        retentionCount = runtime.Configuration.Settings.MessageRetentionCount;
        UpdateLayoutOptions();
        refreshTimer = new() { Interval = SNAPSHOT_REFRESH_INTERVAL };
        refreshTimer.Tick += OnRefreshTimer;
        runtime.Changed += OnChanged;
        runtime.FeedInvalidated += OnMessagesChanged;
        if (kind == MessageKind.Danmu)
        {
            runtime.DanmuCommitted += OnDanmuCommitted;
            runtime.DanmuTranslationCommitted += OnDanmuTranslationCommitted;
        }
        runtime.SpecialAttentionChanged += OnSpecialAttentionChanged;
    }

    /// <summary>切换阅读房间，取消旧房间的图片回写。</summary>
    public async Task SelectRoomAsync(RoomTabState? selected, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (disposed)
        {
            return;
        }
        Volatile.Write(ref room, selected);
        OnPropertyChanged(nameof(SelectedRoom));
        OnPropertyChanged(nameof(HasSelectedRoom));
        ResetWindow();
        UnreadCount = 0;
        await RefreshCoreAsync(requestCancellation: cancellationToken);
    }

    private void ResetWindow()
    {
        IsReadingHistory = false;
        revision++;
        loadingRevision++;
        IsLoadingHistory = false;
        historyLoadTask = null;
        hasLoadedRoom = false;
        keepHistoryWindow = false;
        keepPinnedWindow = false;
        showNewest = false;
        retainedHistory = null;
        retainedPins = null;
        SetPages(null, null);
        retentionCount = runtime.Configuration.Settings.MessageRetentionCount;
        translationRequested.Clear();
        presentationQueue.Clear();
        presentationQueue = new();
        ClearLiveQueue();
        lifetime.Cancel();
        lifetime.Dispose();
        lifetime = new();
        Items.Clear();
        PinnedItems.Clear();
        ScrollingItems.Clear();
        ScrollingRows.Clear();
        NotifyRegions();
    }

    /// <summary>确认当前房间、类型的全部未读记录，然后刷新当前选择。</summary>
    public async Task MarkAllReadAsync()
    {
        var current = room;
        if (disposed || current is null)
        {
            return;
        }
        await runtime.Messages.MarkAllReadAsync(current.Id, kind);
        runtime.NotifyTranslation();
        await RefreshAsync();
    }

    /// <summary>清空已确认的目标房间；仍在阅读该房间时重置分页、置顶区和滚动状态。</summary>
    public async Task ClearAsync(RoomTabState target)
    {
        if (disposed)
        {
            return;
        }
        if (room?.Id == target.Id)
        {
            CancelHistoryPage();
        }
        await runtime.ClearFeedAsync(target, kind);
        if (!disposed && room?.Id == target.Id)
        {
            await SelectRoomAsync(room);
        }
    }

    /// <summary>将最新数据库投影应用到已有行。</summary>
    public Task RefreshAsync()
    {
        return RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync(MessageFeedPageDirection? direction = null, bool pagePinned = false,
        string? targetId = null, CancellationToken requestCancellation = default, bool resetPinnedWindow = false)
    {
        if (disposed)
        {
            return;
        }
        if (UpdateSortOrder())
        {
            ResetWindow();
        }
        UpdateLayoutOptions();
        var current = room;
        var version = revision;
        if (disposed || current is null)
        {
            return;
        }
        var isPage = direction is MessageFeedPageDirection.Before or MessageFeedPageDirection.After;
        using var request = isPage || requestCancellation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, requestCancellation) : null;
        var cancellationToken = request?.Token ?? lifetime.Token;
        if (isPage)
        {
            historyPageRequest = request;
        }
        var loading = isPage || direction == MessageFeedPageDirection.Around || !hasLoadedRoom ? ++loadingRevision : 0;
        var entered = false;
        var viewportUpdateStarted = false;
        IDisposable? loadingHint = null;
        try
        {
            if (loading != 0)
            {
                IsLoadingHistory = true;
            }
            await refreshGate.WaitAsync(cancellationToken);
            entered = true;
            if (disposed || version != revision || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (loading != 0)
            {
                viewportUpdateStarted = true;
                BeginViewportUpdate();
                loadingHint = DispatcherTimer.RunOnce(() =>
                {
                    if (WindowIsCurrent(version, cancellationToken) && loading == loadingRevision && !pagePinned)
                    {
                        AddLoadingRows(direction == MessageFeedPageDirection.After);
                    }
                }, TimeSpan.FromMilliseconds(LOADING_HINT_DELAY_MILLISECONDS), DispatcherPriority.Background);
            }
            while (WindowIsCurrent(version, cancellationToken))
            {
                if (SortOrder != runtime.Configuration.Settings.MessageSorting.GetOrder(kind, ReadFilter))
                {
                    RequestSnapshotRefresh();
                    return;
                }
                var context = CaptureProjectionContext();
                var watchAnchor = kind == MessageKind.Danmu && hasLoadedRoom && !keepHistoryWindow && direction is null;
                var anchor = watchAnchor ? GetReadingAnchor() : null;
                var result = await ReadWindowAsync(current, direction, pagePinned, targetId, anchor, cancellationToken,
                    resetPinnedWindow);
                var snapshot = result.Window;
                if (!WindowIsCurrent(version, cancellationToken))
                {
                    return;
                }
                if (!ProjectionOptionsAreCurrent(context))
                {
                    continue;
                }
                loadingHint?.Dispose();
                loadingHint = null;
                if (direction == MessageFeedPageDirection.Around && snapshot.Scrolling.Entries.Count == 0)
                {
                    return;
                }

                var plan = await PlanWindowAsync(snapshot, context, cancellationToken);
                if (!WindowIsCurrent(version, cancellationToken))
                {
                    return;
                }
                if (!ProjectionOptionsAreCurrent(context, checkDisplay: true))
                {
                    continue;
                }
                if (plan.HasChanges)
                {
                    if (!viewportUpdateStarted)
                    {
                        viewportUpdateStarted = true;
                        BeginViewportUpdate();
                    }
                    if (!await ApplyWindowAsync(plan, context, version, cancellationToken, ReadingPositionIsCurrent))
                    {
                        continue;
                    }
                }
                else if (!ReadingPositionIsCurrent())
                {
                    continue;
                }
                displaySettings = context.Display;
                displayFilterVersion = context.FilterVersion;
                retentionCount = context.RetentionCount;
                PublishWindow(result, direction, pagePinned, resetPinnedWindow);
                return;

                bool ReadingPositionIsCurrent()
                {
                    if (!watchAnchor)
                    {
                        return true;
                    }
                    var currentAnchor = GetReadingAnchor();
                    return ReferenceEquals(currentAnchor, anchor) || !result.ReadingWindowPreserved &&
                        (currentAnchor is null || snapshot.Scrolling.Entries.Any(entry => entry.Record.Id == currentAnchor.Record.Id));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (isPage && ReferenceEquals(historyPageRequest, request))
            {
                historyPageRequest = null;
            }
            loadingHint?.Dispose();
            try
            {
                if (loading != 0 && loading == loadingRevision && version == revision)
                {
                    RemoveLoadingRows();
                    IsLoadingHistory = false;
                }
            }
            finally
            {
                try
                {
                    if (viewportUpdateStarted)
                    {
                        EndViewportUpdate();
                    }
                }
                finally
                {
                    if (entered)
                    {
                        refreshGate.Release();
                    }
                }
            }
        }
    }

    private void PublishWindow(MessageFeedWindowResult result, MessageFeedPageDirection? direction, bool pagePinned,
        bool resetPinnedWindow)
    {
        var snapshot = result.Window;
        if (direction == MessageFeedPageDirection.Latest)
        {
            keepHistoryWindow = false;
            if (resetPinnedWindow)
            {
                keepPinnedWindow = false;
            }
            showNewest = true;
        }
        else if (direction is MessageFeedPageDirection.Before or MessageFeedPageDirection.After or MessageFeedPageDirection.Around)
        {
            if (pagePinned)
            {
                keepPinnedWindow = true;
            }
            else
            {
                keepHistoryWindow = true;
            }
        }
        else if (result.ReadingWindowPreserved)
        {
            keepHistoryWindow = true;
        }
        retainedHistory = RetainedRange(snapshot.Scrolling, result.ScrollingRequest);
        retainedPins = RetainedRange(snapshot.Pinned, result.PinnedRequest);
        SetPages(snapshot.Scrolling, snapshot.Pinned);
        UnreadCount = snapshot.UnreadCount;
        NotifyRegions();
        if (!hasLoadedRoom)
        {
            hasLoadedRoom = true;
            InitialLoadCompleted?.Invoke();
        }
    }

    private void BeginViewportUpdate()
    {
        ViewportUpdateDepth++;
        ViewportUpdateStarted?.Invoke();
    }

    private void EndViewportUpdate()
    {
        ViewportUpdateDepth--;
        ViewportUpdateCompleted?.Invoke();
    }

    private void NotifyRegions()
    {
        SetProperty(ref hasPinnedItems, PinnedItems.Count > 0, nameof(HasPinnedItems));
        SetProperty(ref hasScrollingItems, ScrollingRows.Count > 0, nameof(HasScrollingItems));
    }

    private void UpdateLayoutOptions()
    {
        var settings = runtime.Configuration.Settings;
        SetProperty(ref isCompactDisplay, settings.CompactDanmuDisplay, nameof(IsCompactDisplay));
        SetProperty(ref isCompactMessageWrap, settings.CompactDanmuWrap, nameof(IsCompactMessageWrap));
        SetProperty(ref isCompactSuperChatWrap, settings.CompactSuperChatWrap, nameof(IsCompactSuperChatWrap));
        SetProperty(ref messageTextScale, settings.MessageTextScale, nameof(MessageTextScale));
        SetProperty(ref showUserAvatars, settings.ShowUserAvatars, nameof(ShowUserAvatars));
        SetProperty(ref showFanMedals, settings.ShowFanMedals, nameof(ShowFanMedals));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        Volatile.Write(ref disposed, true);
        ReadVisibleAnchor = null;
        IsVisibleForPresentation = null;
        IsLoadingHistory = false;
        RemoveLoadingRows();
        revision++;
        Volatile.Write(ref room, null);
        runtime.Changed -= OnChanged;
        runtime.FeedInvalidated -= OnMessagesChanged;
        if (kind == MessageKind.Danmu)
        {
            runtime.DanmuCommitted -= OnDanmuCommitted;
            runtime.DanmuTranslationCommitted -= OnDanmuTranslationCommitted;
        }
        ClearLiveQueue();
        presentationQueue.Clear();
        runtime.SpecialAttentionChanged -= OnSpecialAttentionChanged;
        refreshTimer.Stop();
        refreshTimer.Tick -= OnRefreshTimer;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void OnChanged(string roomId)
    {
        var current = Volatile.Read(ref room);
        if (Volatile.Read(ref disposed) || current is null || (roomId.Length > 0 && roomId != current.Id))
        {
            return;
        }
        if (roomId.Length == 0)
        {
            Interlocked.Increment(ref projectionOptionsRevision);
        }
        RequestSnapshotRefresh();
    }

    private void RequestSnapshotRefresh()
    {
        Interlocked.Exchange(ref refreshRequested, 1);
        ScheduleRefresh();
    }

    private void OnMessagesChanged(string roomId, MessageKind? changedKind)
    {
        if (kind == MessageKind.Danmu || changedKind is null || changedKind == kind)
        {
            OnChanged(roomId);
        }
    }

    private void ScheduleRefresh()
    {
        if (!Volatile.Read(ref disposed) && Interlocked.CompareExchange(ref refreshScheduled, 1, 0) == 0)
        {
            Dispatcher.UIThread.Post(StartRefreshTimer, DispatcherPriority.Background);
        }
    }

    private void StartRefreshTimer()
    {
        if (!disposed)
        {
            refreshTimer.Interval = kind == MessageKind.Danmu && HasLiveChanges()
                ? LIVE_REFRESH_INTERVAL : SNAPSHOT_REFRESH_INTERVAL;
            refreshTimer.Start();
        }
    }

    private async void OnRefreshTimer(object? sender, EventArgs args)
    {
        if (refreshTimer.Interval == LIVE_REFRESH_INTERVAL && IsLiveBatchGrowing())
        {
            return;
        }
        refreshTimer.Stop();
        var requiresSnapshot = Interlocked.Exchange(ref refreshRequested, 0) != 0;
        try
        {
            await FlushChangesAsync(requiresSnapshot);
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
        }
        finally
        {
            Interlocked.Exchange(ref refreshScheduled, 0);
            if (Volatile.Read(ref refreshRequested) != 0 || HasLiveChanges())
            {
                ScheduleRefresh();
            }
        }
    }

    private async Task LoadEmoticonsAsync(MessageItemViewModel item, CancellationToken cancellationToken)
    {
        var record = item.Record;
        if (record.Emoticons.Count == 0)
        {
            return;
        }
        try
        {
            var emoticons = await Task.Run(() => Task.WhenAll(record.Emoticons.Select(async value => new DanmuEmoticon
            {
                Text = value.Text,
                ImagePath = await runtime.Emoticons.GetEmoticonAsync(value.Url, cancellationToken)
            })), cancellationToken);
            var images = emoticons.Where(value => value.ImagePath.Length > 0).ToArray();
            while (IsCurrent())
            {
                var content = item.Detail;
                var annotations = item.ContentPinyin;
                var plan = images.Length > 0 || annotations.Count > 0
                    ? await Task.Run(() => MessageTextRenderPlan.Create(content, images, annotations), cancellationToken)
                    : null;
                if (!IsCurrent())
                {
                    return;
                }
                if (content == item.Detail && ReferenceEquals(annotations, item.ContentPinyin))
                {
                    item.ApplyEmoticons(images, plan);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
        }

        bool IsCurrent()
        {
            return !disposed && !cancellationToken.IsCancellationRequested && ContainsActiveItem(item) &&
                item.Record.Generation == record.Generation && item.Record.Emoticons.SequenceEqual(record.Emoticons);
        }
    }
}
