using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private int displayFilterVersion;

    private bool IsUnchanged(MessageFeedWindow window, MessageDisplaySettings display)
    {
        if (!ReferenceEquals(display, displaySettings) || displayFilterVersion != display.DanmuFilter.StateVersion ||
            PinnedItems.Count != window.Pinned.Entries.Count || ScrollingItems.Count != window.Scrolling.Entries.Count ||
            Items.Count != PinnedItems.Count + ScrollingItems.Count || ScrollingRows.Count != ScrollingItems.Count)
        {
            return false;
        }

        for (var index = 0; index < PinnedItems.Count; index++)
        {
            if (!IsUnchanged(PinnedItems[index], window.Pinned.Entries[index].Record, window, display))
            {
                return false;
            }
        }
        for (var index = 0; index < ScrollingItems.Count; index++)
        {
            var record = window.Scrolling.Entries[DisplayIndex(index, ScrollingItems.Count)].Record;
            if (!ReferenceEquals(ScrollingRows[index], ScrollingItems[index]) ||
                !IsUnchanged(ScrollingItems[index], record, window, display))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsUnchanged(MessageItemViewModel item, MessageRecord record, MessageFeedWindow window,
        MessageDisplaySettings display)
    {
        return ReferenceEquals(item.Record, record) &&
            item.TranslationPending == (HasCurrentPendingTranslation(record, display, window) &&
                !display.IsTranslationBlocked(record));
    }

    private static bool HasCurrentPendingTranslation(MessageRecord record, MessageDisplaySettings display,
        MessageFeedWindow window)
    {
        return record.TranslationFingerprint == display.TranslationFingerprint &&
            window.PendingTranslationIds.Contains(record.Id);
    }

    private async Task ApplyWindowAsync(MessageFeedWindow window, MessageDisplaySettings display, int version,
        CancellationToken cancellationToken)
    {
        displaySettings = display;
        displayFilterVersion = display.DanmuFilter.StateVersion;
        var existing = Items.ToDictionary(item => item.Record.Id);
        var newItems = new List<MessageItemViewModel>();
        var candidates = new List<MessageRecord>();
        var expectedPinned = new MessageItemViewModel[window.Pinned.Entries.Count];
        var expectedScrolling = new MessageItemViewModel[window.Scrolling.Entries.Count];
        var budget = new FeedUiWorkBudget();
        await BuildItemsAsync(window.Pinned.Entries, expectedPinned, sortScrolling: false);
        await BuildItemsAsync(window.Scrolling.Entries, expectedScrolling, sortScrolling: true);
        await FeedCollectionSynchronizer.SynchronizeAsync(Items, [.. expectedPinned, .. expectedScrolling], cancellationToken);
        await FeedCollectionSynchronizer.SynchronizeAsync(PinnedItems, expectedPinned, cancellationToken);
        await FeedCollectionSynchronizer.SynchronizeAsync(ScrollingItems, expectedScrolling, cancellationToken);
        await SynchronizeScrollingRowsAsync(cancellationToken);
        ResetActiveDanmuItems();
        foreach (var item in newItems)
        {
            _ = LoadEmoticonsAsync(item, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var roomToken = lifetime.Token;
        SchedulePresentation(version, roomToken);
        var requested = candidates.Where(record => translationRequested.Add(record.Id + display.TranslationFingerprint)).ToArray();
        TrackBackground(RegisterTranslationsAsync(requested, display.TranslationFingerprint, version, roomToken));

        async Task BuildItemsAsync(IReadOnlyList<MessageFeedEntry> entries, MessageItemViewModel[] destination, bool sortScrolling)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = entries[sortScrolling ? DisplayIndex(index, entries.Count) : index].Record;
                var blocked = display.IsTranslationBlocked(record);
                var pending = !blocked && HasCurrentPendingTranslation(record, display, window);
                if (!blocked && display.TranslationEnabled && record.Kind is MessageKind.Danmu or MessageKind.SuperChat &&
                    record.TranslationFingerprint != display.TranslationFingerprint)
                {
                    candidates.Add(record);
                }

                if (existing.TryGetValue(record.Id, out var item))
                {
                    var changed = !ReferenceEquals(item.Record, record) ||
                        !ReferenceEquals(item.PresentationSettings, display) || item.TranslationPending != pending;
                    item.Apply(record, display, pending);
                    if (changed)
                    {
                        await budget.YieldIfNeededAsync(cancellationToken);
                    }
                }
                else
                {
                    item = MessageItemViewModel.CreateDeferred(record, display, runtime.Pinyin, pending,
                        kind == MessageKind.Danmu && record.Kind != MessageKind.Danmu);
                    newItems.Add(item);
                    await budget.YieldIfNeededAsync(cancellationToken);
                }
                destination[index] = item;
            }
        }
    }

    private int DisplayIndex(int index, int count)
    {
        return SortOrder == MessageSortOrder.NewestFirst ? count - index - 1 : index;
    }
}
