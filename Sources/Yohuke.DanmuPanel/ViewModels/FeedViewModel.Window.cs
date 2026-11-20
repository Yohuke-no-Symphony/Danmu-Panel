using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    /// <summary>加载与当前窗口相邻的较早记录。</summary>
    public Task LoadMoreAsync()
    {
        return NavigatePageAsync(MessageFeedPageDirection.Before);
    }

    /// <summary>从当前窗口继续向较新的记录翻页。</summary>
    public Task LoadNewerAsync()
    {
        return NavigatePageAsync(MessageFeedPageDirection.After);
    }

    internal Task LoadOlderPinnedAsync()
    {
        return NavigatePageAsync(MessageFeedPageDirection.Before, pagePinned: true);
    }

    internal Task LoadNewerPinnedAsync()
    {
        return NavigatePageAsync(MessageFeedPageDirection.After, pagePinned: true);
    }

    internal Task ShowLatestAsync()
    {
        return RefreshCoreAsync(MessageFeedPageDirection.Latest);
    }

    internal void ResumeLiveDanmuAtLatest()
    {
        if (kind == MessageKind.Danmu && !HasNewerHistory)
        {
            keepHistoryWindow = false;
        }
    }

    private Task NavigatePageAsync(MessageFeedPageDirection direction, bool pagePinned = false)
    {
        if (disposed || room is null)
        {
            return Task.CompletedTask;
        }
        if (historyLoadTask is { IsCompleted: false })
        {
            return historyLoadTask;
        }
        historyLoadTask = RefreshCoreAsync(direction, pagePinned);
        return historyLoadTask;
    }

    private async Task<(MessageFeedWindow Window, MessageFeedPageRequest History, MessageFeedPageRequest? Pins)> ReadWindowAsync(
        RoomTabState current, MessageFeedPageDirection? direction,
        bool pagePinned, string? targetId, CancellationToken cancellationToken)
    {
        var settings = runtime.Configuration.Settings;
        var resized = retentionCount != settings.MessageRetentionCount;
        retentionCount = settings.MessageRetentionCount;
        var capacity = retentionCount + BUFFERED_HISTORY_PAGES * HISTORY_PAGE_SIZE;
        var previous = scrollingPage;
        var previousPinned = pinnedPage;
        var resetToLatest = direction == MessageFeedPageDirection.Latest;
        var pauseScrolling = kind == MessageKind.Danmu ? keepHistoryWindow :
            IsReadingHistory && (previous?.Entries.Count >= retentionCount || previous?.HasNewer == true);
        var preserveScrolling = !resized && !resetToLatest &&
            (pauseScrolling || keepHistoryWindow && previous?.HasNewer == true || pagePinned);
        var initialLimit = keepHistoryWindow && !resized && !resetToLatest
            ? Math.Clamp(previous?.Entries.Count ?? retentionCount, retentionCount, capacity) : retentionCount;
        var start = kind != MessageKind.Danmu && ReadFilter == MessageReadFilter.Unread &&
            SortOrder == MessageSortOrder.OldestFirst && !showNewest && !resetToLatest
            ? MessageFeedPageDirection.Oldest : MessageFeedPageDirection.Latest;
        var adjacent = direction is MessageFeedPageDirection.Before or MessageFeedPageDirection.After;
        var history = retainedHistory;
        if ((preserveScrolling || adjacent && !pagePinned) && history is null && kind == MessageKind.Danmu && ScrollingItems.Count > 0)
        {
            var bounds = await runtime.Messages.GetTimelineBoundsAsync(current.Id, ScrollingItems[0].Record.Id,
                ScrollingItems[^1].Record.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (bounds is { } range)
            {
                history = new(capacity, MessageFeedPageDirection.Range, range.First, range.Last);
                previous = (await runtime.Messages.LoadWindowAsync(current.Id, kind, history,
                    hideEmoticonDanmu: settings.HideEmoticonDanmu, danmuRegexFilter: runtime.DanmuFilter,
                    cancellationToken: cancellationToken)).Scrolling;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        var scrolling = preserveScrolling && history is { } currentHistory
            ? currentHistory with { Limit = capacity }
            : new MessageFeedPageRequest(initialLimit, start);
        MessageFeedPageRequest? pinned = null;
        if (kind == MessageKind.Danmu)
        {
            pinned = keepPinnedWindow && !resetToLatest && retainedPins is { } currentPins
                ? currentPins with { Limit = HISTORY_PAGE_SIZE }
                : new(HISTORY_PAGE_SIZE, MessageFeedPageDirection.Oldest);
        }

        if (adjacent && pagePinned && previousPinned is { Entries.Count: > 0 })
        {
            pinned = Adjacent(retainedPins!, direction!.Value);
        }
        else if (adjacent && !pagePinned && history is not null && previous is { Entries.Count: > 0 })
        {
            scrolling = Adjacent(history, direction!.Value);
        }
        else if (direction == MessageFeedPageDirection.Around)
        {
            scrolling = new(retentionCount, direction.Value, TargetId: targetId);
        }
        var filter = runtime.DanmuFilter;
        var snapshot = await LoadAsync(scrolling, pinned);
        cancellationToken.ThrowIfCancellationRequested();
        if (adjacent && pagePinned && previousPinned is { Entries.Count: > 0 })
        {
            if (snapshot.Pinned.Entries.Count == 0)
            {
                pinned = retainedPins! with { Limit = HISTORY_PAGE_SIZE };
                snapshot = await LoadAsync(scrolling, pinned);
            }
        }
        else if (adjacent && !pagePinned && history is not null && previous is { Entries.Count: > 0 })
        {
            var before = direction == MessageFeedPageDirection.Before;
            var combined = before ? snapshot.Scrolling.Entries.Concat(previous.Entries) :
                previous.Entries.Concat(snapshot.Scrolling.Entries);
            var bounded = (before ? combined.Take(capacity) : combined.TakeLast(capacity)).ToArray();
            scrolling = Range(bounded, capacity);
            snapshot = await LoadAsync(scrolling, pinned);
        }
        else if (direction is null && kind != MessageKind.Danmu && !resized && !preserveScrolling &&
            IsReadingHistory && !pauseScrolling && history is { } oldRange && previous?.Entries.Count >= retentionCount)
        {
            scrolling = oldRange with { Limit = capacity };
            snapshot = await LoadAsync(scrolling, pinned);
        }

        var adjacentHistory = AdjacentToEmptyRange(snapshot.Scrolling, scrolling, retentionCount);
        var adjacentPins = AdjacentToEmptyRange(snapshot.Pinned, pinned, HISTORY_PAGE_SIZE);
        if (adjacentHistory is not null || adjacentPins is not null)
        {
            scrolling = adjacentHistory ?? scrolling;
            pinned = adjacentPins ?? pinned;
            snapshot = await LoadAsync(scrolling, pinned);
        }
        return (snapshot, scrolling, pinned);

        Task<MessageFeedWindow> LoadAsync(MessageFeedPageRequest page, MessageFeedPageRequest? priority)
        {
            return runtime.Messages.LoadWindowAsync(current.Id, kind, page, priority, ReadFilter,
                settings.HideEmoticonDanmu, filter, cancellationToken);
        }
    }

    private void SetPages(MessageFeedPage? ordinary, MessageFeedPage? pinned)
    {
        var hadNewer = HasNewerHistory;
        var hadOlderPinned = HasOlderPinnedMessages;
        var hadNewerPinned = HasNewerPinnedMessages;
        scrollingPage = ordinary;
        pinnedPage = pinned;
        if (hadNewer != HasNewerHistory)
        {
            OnPropertyChanged(nameof(HasNewerHistory));
        }
        if (hadOlderPinned != HasOlderPinnedMessages)
        {
            OnPropertyChanged(nameof(HasOlderPinnedMessages));
        }
        if (hadNewerPinned != HasNewerPinnedMessages)
        {
            OnPropertyChanged(nameof(HasNewerPinnedMessages));
        }
    }

    private static MessageFeedPageRequest Range(IReadOnlyList<MessageFeedEntry> entries, int capacity)
    {
        return new(capacity, MessageFeedPageDirection.Range, entries[0].Cursor, entries[^1].Cursor);
    }

    private static MessageFeedPageRequest? RetainedRange(MessageFeedPage page, MessageFeedPageRequest? request)
    {
        if (page.Entries.Count == 0)
        {
            return null;
        }
        return request is { Direction: MessageFeedPageDirection.Range } ? request : Range(page.Entries, page.Entries.Count);
    }

    private static MessageFeedPageRequest? AdjacentToEmptyRange(MessageFeedPage page, MessageFeedPageRequest? request, int limit)
    {
        if (page.Entries.Count > 0 || request is not { Direction: MessageFeedPageDirection.Range })
        {
            return null;
        }
        if (page.HasOlder)
        {
            return new(limit, MessageFeedPageDirection.Before, request.Boundary);
        }
        return page.HasNewer ? new(limit, MessageFeedPageDirection.After, request.Through) : null;
    }

    private static MessageFeedPageRequest Adjacent(MessageFeedPageRequest current, MessageFeedPageDirection direction)
    {
        return new(HISTORY_PAGE_SIZE, direction,
            direction == MessageFeedPageDirection.Before ? current.Boundary : current.Through);
    }
}
