using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>分页加载并原位刷新消息列表。</summary>
public sealed class FeedViewModel : ObservableObject, IDisposable
{
    private const int PAGE_SIZE = 100;
    private readonly PanelRuntime runtime;
    private readonly MessageKind kind;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly DispatcherTimer refreshTimer;
    private CancellationTokenSource lifetime = new();
    private RoomTabState? room;
    private int limit = PAGE_SIZE;
    private int revision;
    private int unreadCount;
    private int refreshRequested;
    private int refreshScheduled;
    private bool disposed;
    private bool hasPinnedItems;
    private bool hasScrollingItems;
    private bool isCompactDisplay;
    private bool isCompactMessageWrap;
    private bool isCompactSuperChatWrap;
    private double messageTextScale = 1;
    private MessageDisplaySettings? displaySettings;
    private readonly HashSet<string> translationRequested = new();
    public ObservableCollection<MessageItemViewModel> Items { get; } = [];
    public ObservableCollection<MessageItemViewModel> PinnedItems { get; } = [];
    public ObservableCollection<MessageItemViewModel> ScrollingItems { get; } = [];
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
    public event Action<Exception>? Failed;

    public bool IsCompactDisplay => isCompactDisplay;
    public bool IsCompactMessageWrap => isCompactMessageWrap;
    public bool IsCompactSuperChatWrap => isCompactSuperChatWrap;
    public double MessageTextScale => messageTextScale;

    /// <summary>连接一个独立消息类型的列表。</summary>
    public FeedViewModel(PanelRuntime runtime, MessageKind kind)
    {
        this.runtime = runtime;
        this.kind = kind;
        UpdateLayoutOptions();
        refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
        refreshTimer.Tick += OnRefreshTimer;
        runtime.Changed += OnChanged;
        runtime.MessagesChanged += OnMessagesChanged;
    }

    /// <summary>切换阅读房间，取消旧房间的图片回写。</summary>
    public async Task SelectRoomAsync(RoomTabState? selected)
    {
        if (disposed)
        {
            return;
        }
        Volatile.Write(ref room, selected);
        OnPropertyChanged(nameof(SelectedRoom));
        OnPropertyChanged(nameof(HasSelectedRoom));
        IsReadingHistory = false;
        revision++;
        limit = PAGE_SIZE;
        translationRequested.Clear();
        lifetime.Cancel();
        lifetime.Dispose();
        lifetime = new();
        Items.Clear();
        PinnedItems.Clear();
        ScrollingItems.Clear();
        UnreadCount = 0;
        NotifyRegions();
        await RefreshAsync();
    }

    /// <summary>继续加载更早的记录。</summary>
    public async Task LoadMoreAsync()
    {
        if (disposed)
        {
            return;
        }
        limit = Math.Max(limit, ScrollingItems.Count(item => !item.Record.IsPriority)) + PAGE_SIZE;
        await RefreshAsync();
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
        await runtime.ClearFeedAsync(target, kind);
        if (!disposed && room?.Id == target.Id)
        {
            await SelectRoomAsync(room);
        }
    }

    /// <summary>将最新数据库投影应用到已有行。</summary>
    public async Task RefreshAsync()
    {
        if (disposed)
        {
            return;
        }
        UpdateLayoutOptions();
        var current = room;
        var version = revision;
        if (disposed || current is null)
        {
            return;
        }
        var cancellationToken = lifetime.Token;
        try
        {
            await refreshGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            var snapshot = await runtime.Messages.LoadFeedAsync(current.Id, kind, limit,
                IsReadingHistory ? ScrollingItems.FirstOrDefault(item => !item.Record.IsPriority)?.Record.Id : null,
                cancellationToken);
            if (disposed || version != revision || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            var records = snapshot.Records;
            var currentSettings = runtime.Configuration.Settings;
            displaySettings = MessageDisplaySettings.Capture(currentSettings, displaySettings);
            var translationEnabled = currentSettings.Translation.Enabled;
            var existingItems = Items.ToDictionary(item => item.Record.Id);
            var translationWorkCreated = false;
            var expected = records.Select(record => record.Id).ToHashSet();
            for (var index = Items.Count - 1; index >= 0; index--)
            {
                if (!expected.Contains(Items[index].Record.Id))
                {
                    Items.RemoveAt(index);
                }
            }
            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                var translationPending = record.TranslationFingerprint == displaySettings.TranslationFingerprint &&
                    snapshot.PendingTranslationIds.Contains(record.Id);
                if (translationEnabled && record.Kind is MessageKind.Danmu or MessageKind.SuperChat)
                {
                    var fingerprint = displaySettings.TranslationFingerprint;
                    if (record.TranslationFingerprint != fingerprint && translationRequested.Add(record.Id + fingerprint))
                    {
                        var queued = await runtime.Messages.UpsertTranslationJobAsync(record.Id, record.Generation, fingerprint, cancellationToken);
                        if (disposed || version != revision || cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        translationPending = queued;
                        translationWorkCreated |= queued;
                    }
                }
                if (!existingItems.TryGetValue(record.Id, out var existing))
                {
                    var item = MessageItemViewModel.Create(record, displaySettings, runtime.Pinyin, translationPending);
                    Items.Insert(index, item);
                    _ = LoadEmoticonsAsync(item, cancellationToken);
                }
                else
                {
                    if (!ReferenceEquals(Items[index], existing))
                    {
                        var oldIndex = Items.IndexOf(existing);
                        Items.Move(oldIndex, index);
                    }
                    existing.Apply(record, displaySettings, translationPending);
                }
            }
            if (translationWorkCreated)
            {
                runtime.NotifyTranslation();
            }
            SynchronizeRegion(PinnedItems, Items.Where(item => kind == MessageKind.Danmu && item.Record.IsPinned).ToArray());
            SynchronizeRegion(ScrollingItems, Items.Where(item => kind != MessageKind.Danmu || !item.Record.IsPinned).ToArray());
            UnreadCount = snapshot.UnreadCount;
            NotifyRegions();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private static void SynchronizeRegion(ObservableCollection<MessageItemViewModel> target,
        IReadOnlyList<MessageItemViewModel> expected)
    {
        var retained = expected.ToHashSet();
        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!retained.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }
        for (var index = 0; index < expected.Count; index++)
        {
            var item = expected[index];
            if (index < target.Count && ReferenceEquals(target[index], item))
            {
                continue;
            }
            var previousIndex = target.IndexOf(item);
            if (previousIndex < 0)
            {
                target.Insert(index, item);
            }
            else if (previousIndex != index)
            {
                target.Move(previousIndex, index);
            }
        }
    }

    private void NotifyRegions()
    {
        SetProperty(ref hasPinnedItems, PinnedItems.Count > 0, nameof(HasPinnedItems));
        SetProperty(ref hasScrollingItems, ScrollingItems.Count > 0, nameof(HasScrollingItems));
    }

    private void UpdateLayoutOptions()
    {
        var settings = runtime.Configuration.Settings;
        SetProperty(ref isCompactDisplay, settings.CompactDanmuDisplay, nameof(IsCompactDisplay));
        SetProperty(ref isCompactMessageWrap, settings.CompactDanmuWrap, nameof(IsCompactMessageWrap));
        SetProperty(ref isCompactSuperChatWrap, settings.CompactSuperChatWrap, nameof(IsCompactSuperChatWrap));
        SetProperty(ref messageTextScale, settings.MessageTextScale, nameof(MessageTextScale));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        Volatile.Write(ref disposed, true);
        revision++;
        Volatile.Write(ref room, null);
        runtime.Changed -= OnChanged;
        runtime.MessagesChanged -= OnMessagesChanged;
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
        Interlocked.Exchange(ref refreshRequested, 1);
        ScheduleRefresh();
    }

    private void OnMessagesChanged(string roomId, MessageKind? changedKind)
    {
        if (changedKind is null || changedKind == kind)
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
            refreshTimer.Start();
        }
    }

    private async void OnRefreshTimer(object? sender, EventArgs args)
    {
        refreshTimer.Stop();
        Interlocked.Exchange(ref refreshRequested, 0);
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
        }
        finally
        {
            Interlocked.Exchange(ref refreshScheduled, 0);
            if (Volatile.Read(ref refreshRequested) != 0)
            {
                ScheduleRefresh();
            }
        }
    }

    private async Task LoadEmoticonsAsync(MessageItemViewModel item, CancellationToken cancellationToken)
    {
        try
        {
            var emoticons = await Task.WhenAll(item.Record.Emoticons.Select(async value => new DanmuEmoticon
            {
                Text = value.Text,
                ImagePath = await runtime.Emoticons.GetEmoticonAsync(value.Url, cancellationToken)
            }));
            if (!disposed && !cancellationToken.IsCancellationRequested && Items.Contains(item))
            {
                item.Emoticons = emoticons.Where(value => value.ImagePath.Length > 0).ToArray();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
        }
    }
}
