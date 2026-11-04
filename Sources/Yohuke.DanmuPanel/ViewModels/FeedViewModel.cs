using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;

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
    private bool disposed;
    private readonly HashSet<string> translationRequested = new();
    public ObservableCollection<MessageItemViewModel> Items { get; } = [];
    public ObservableCollection<MessageItemViewModel> PinnedItems { get; } = [];
    public ObservableCollection<MessageItemViewModel> ScrollingItems { get; } = [];
    public bool HasPinnedItems => PinnedItems.Count > 0;
    public bool HasScrollingItems => ScrollingItems.Count > 0;
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
    public bool IsReadingHistory { get; set; }
    public event Action<Exception>? Failed;

    public bool IsCompactDisplay => runtime.Configuration.Settings.CompactDanmuDisplay;
    public bool IsCompactMessageWrap => runtime.Configuration.Settings.CompactDanmuWrap;
    public bool IsCompactSuperChatWrap => runtime.Configuration.Settings.CompactSuperChatWrap;

    /// <summary>连接一个独立消息类型的列表。</summary>
    public FeedViewModel(PanelRuntime runtime, MessageKind kind)
    {
        this.runtime = runtime;
        this.kind = kind;
        refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
        refreshTimer.Tick += OnRefreshTimer;
        runtime.Changed += OnChanged;
    }

    /// <summary>切换阅读房间，取消旧房间的图片回写。</summary>
    public async Task SelectRoomAsync(RoomTabState? selected)
    {
        if (disposed)
        {
            return;
        }
        room = selected;
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

    /// <summary>将最新数据库投影应用到已有行。</summary>
    public async Task RefreshAsync()
    {
        if (disposed)
        {
            return;
        }
        OnPropertyChanged(nameof(IsCompactDisplay));
        OnPropertyChanged(nameof(IsCompactMessageWrap));
        OnPropertyChanged(nameof(IsCompactSuperChatWrap));
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
                IsReadingHistory ? ScrollingItems.LastOrDefault(item => !item.Record.IsPriority)?.Record.Id : null,
                cancellationToken);
            if (disposed || version != revision || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            var records = snapshot.Records;
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
                var translationSettings = runtime.Configuration.Settings.Translation;
                if (translationSettings.Enabled && record.Kind is MessageKind.Danmu or MessageKind.SuperChat)
                {
                    var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(translationSettings);
                    if (record.TranslationFingerprint != fingerprint && translationRequested.Add(record.Id + fingerprint))
                    {
                        await runtime.Messages.UpsertTranslationJobAsync(record.Id, record.Generation, fingerprint, cancellationToken);
                        if (disposed || version != revision || cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        runtime.NotifyTranslation();
                    }
                }
                var existing = Items.FirstOrDefault(item => item.Record.Id == record.Id);
                if (existing is null)
                {
                    var item = new MessageItemViewModel(record, runtime.Configuration.Settings, runtime.Pinyin);
                    Items.Insert(index, item);
                    _ = LoadEmoticonsAsync(item, cancellationToken);
                }
                else
                {
                    var oldIndex = Items.IndexOf(existing);
                    if (oldIndex != index)
                    {
                        Items.Move(oldIndex, index);
                    }
                    existing.Update(record, runtime.Configuration.Settings);
                }
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
        OnPropertyChanged(nameof(HasPinnedItems));
        OnPropertyChanged(nameof(HasScrollingItems));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        revision++;
        room = null;
        runtime.Changed -= OnChanged;
        refreshTimer.Stop();
        refreshTimer.Tick -= OnRefreshTimer;
        lifetime.Cancel();
        lifetime.Dispose();
    }

    private void OnChanged(string roomId)
    {
        if (disposed)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!disposed && (roomId.Length == 0 || roomId == room?.Id))
            {
                refreshTimer.Start();
            }
        });
    }

    private async void OnRefreshTimer(object? sender, EventArgs args)
    {
        refreshTimer.Stop();
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Failed?.Invoke(exception);
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
