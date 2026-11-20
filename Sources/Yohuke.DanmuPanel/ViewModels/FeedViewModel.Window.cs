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
        CancelHistoryPage();
        return RefreshCoreAsync(MessageFeedPageDirection.Latest);
    }

    internal void CancelHistoryPage()
    {
        historyPageRequest?.Cancel();
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

    private MessageItemViewModel? GetReadingAnchor()
    {
        return IsReadingHistory ? ReadVisibleAnchor?.Invoke() : null;
    }

    private Task<MessageFeedWindowResult> ReadWindowAsync(
        RoomTabState current, MessageFeedPageDirection? direction,
        bool pagePinned, string? targetId, MessageItemViewModel? anchor, CancellationToken cancellationToken)
    {
        var settings = runtime.Configuration.Settings;
        var requestedRetentionCount = settings.MessageRetentionCount;
        var resized = retentionCount != requestedRetentionCount;
        var capacity = requestedRetentionCount + BUFFERED_HISTORY_PAGES * HISTORY_PAGE_SIZE;
        var preserveReadingRange = kind == MessageKind.Danmu && IsReadingHistory && keepHistoryWindow && direction is null;
        if (preserveReadingRange)
        {
            capacity = Math.Max(capacity, ScrollingItems.Count);
        }
        var previous = scrollingPage;
        var previousPinned = pinnedPage;
        var resetToLatest = direction == MessageFeedPageDirection.Latest;
        var pauseScrolling = kind == MessageKind.Danmu ? keepHistoryWindow :
            IsReadingHistory && (previous?.Entries.Count >= requestedRetentionCount || previous?.HasNewer == true);
        var preserveScrolling = preserveReadingRange || !resized && !resetToLatest &&
            (pauseScrolling || keepHistoryWindow && previous?.HasNewer == true || pagePinned);
        var initialLimit = keepHistoryWindow && !resized && !resetToLatest
            ? Math.Clamp(previous?.Entries.Count ?? requestedRetentionCount, requestedRetentionCount, capacity)
            : requestedRetentionCount;
        var start = kind != MessageKind.Danmu && ReadFilter == MessageReadFilter.Unread &&
            SortOrder == MessageSortOrder.OldestFirst && !showNewest && !resetToLatest
            ? MessageFeedPageDirection.Oldest : MessageFeedPageDirection.Latest;
        var adjacent = direction is MessageFeedPageDirection.Before or MessageFeedPageDirection.After;
        var history = retainedHistory;
        var scrollingDirection = adjacent && !pagePinned ? direction : null;
        if (resized && scrollingDirection is not null && !keepHistoryWindow && !IsReadingHistory)
        {
            if ((start, scrollingDirection) is (MessageFeedPageDirection.Latest, MessageFeedPageDirection.Before)
                or (MessageFeedPageDirection.Oldest, MessageFeedPageDirection.After))
            {
                initialLimit += HISTORY_PAGE_SIZE;
            }
            scrollingDirection = null;
        }
        (string FirstId, string LastId, bool Preserve)? liveAnchors = null;
        if (direction != MessageFeedPageDirection.Around && (preserveScrolling || scrollingDirection is not null) &&
            history is null && kind == MessageKind.Danmu && ScrollingItems.Count > 0)
        {
            liveAnchors = (ScrollingItems[0].Record.Id, ScrollingItems[^1].Record.Id, preserveScrolling);
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

        MessageFeedPageRequest? emptyPinnedFallback = null;
        if (adjacent && pagePinned && previousPinned is { Entries.Count: > 0 })
        {
            pinned = Adjacent(retainedPins!, direction!.Value);
            emptyPinnedFallback = retainedPins! with { Limit = HISTORY_PAGE_SIZE };
        }
        else if (direction == MessageFeedPageDirection.Around)
        {
            scrolling = new(requestedRetentionCount, direction.Value, TargetId: targetId);
        }

        var navigation = new MessageFeedWindowNavigation(scrolling, pinned, capacity, requestedRetentionCount, HISTORY_PAGE_SIZE,
            scrollingDirection, history, previous?.Entries, emptyPinnedFallback, liveAnchors,
            ReadingAnchor: anchor is not null && ScrollingItems.Count > 0
                ? (anchor.Record.Id, ScrollingItems[0].Record.Id, ScrollingItems[^1].Record.Id, ScrollingItems.Count) : null);
        return runtime.Messages.LoadNavigatedWindowAsync(current.Id, kind, navigation, ReadFilter,
            settings.HideEmoticonDanmu, runtime.DanmuFilter, cancellationToken);
    }

    private void SetPages(MessageFeedPage? ordinary, MessageFeedPage? pinned)
    {
        var hadOlder = HasOlderHistory;
        var hadNewer = HasNewerHistory;
        var hadOlderPinned = HasOlderPinnedMessages;
        var hadNewerPinned = HasNewerPinnedMessages;
        scrollingPage = ordinary;
        pinnedPage = pinned;
        if (hadOlder != HasOlderHistory)
        {
            OnPropertyChanged(nameof(HasOlderHistory));
        }
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

    private static MessageFeedPageRequest Adjacent(MessageFeedPageRequest current, MessageFeedPageDirection direction)
    {
        return new(HISTORY_PAGE_SIZE, direction,
            direction == MessageFeedPageDirection.Before ? current.Boundary : current.Through);
    }
}
