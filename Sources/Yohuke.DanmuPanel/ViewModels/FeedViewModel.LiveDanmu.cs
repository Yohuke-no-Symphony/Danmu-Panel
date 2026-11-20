using System.Diagnostics;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private static readonly TimeSpan LIVE_BURST_QUIET_INTERVAL = TimeSpan.FromMilliseconds(50);
    private readonly object liveGate = new();
    private readonly Queue<MessageRecord> liveDanmu = new();
    private readonly Queue<TranslationCompletedEventArgs> liveTranslations = new();
    private readonly Dictionary<string, TranslationCompletedEventArgs> earlyTranslations = new(StringComparer.Ordinal);
    private long firstDanmuAt;
    private long lastDanmuAt;

    private void OnDanmuCommitted(MessageRecord message)
    {
        lock (liveGate)
        {
            if (Volatile.Read(ref disposed) || Volatile.Read(ref room)?.Id != message.RoomTabId)
            {
                return;
            }
            lastDanmuAt = Stopwatch.GetTimestamp();
            if (liveDanmu.Count == 0)
            {
                firstDanmuAt = lastDanmuAt;
            }
            if (liveDanmu.Count >= Volatile.Read(ref retentionCount))
            {
                liveDanmu.Dequeue();
                Interlocked.Exchange(ref refreshRequested, 1);
            }
            liveDanmu.Enqueue(message);
        }
        ScheduleRefresh();
    }

    private void OnDanmuTranslationCommitted(TranslationCompletedEventArgs translation)
    {
        lock (liveGate)
        {
            if (Volatile.Read(ref disposed) || Volatile.Read(ref room)?.Id != translation.RoomTabId)
            {
                return;
            }
            if (liveTranslations.Count >= Volatile.Read(ref retentionCount))
            {
                liveTranslations.Dequeue();
                Interlocked.Exchange(ref refreshRequested, 1);
            }
            liveTranslations.Enqueue(translation);
        }
        ScheduleRefresh();
    }

    private bool HasLiveChanges()
    {
        lock (liveGate)
        {
            return liveDanmu.Count != 0 || liveTranslations.Count != 0;
        }
    }

    private bool IsLiveBatchGrowing()
    {
        lock (liveGate)
        {
            if (liveDanmu.Count < IMessageStore.MAX_BATCH_COUNT)
            {
                return false;
            }
            var now = Stopwatch.GetTimestamp();
            return Stopwatch.GetElapsedTime(lastDanmuAt, now) < LIVE_BURST_QUIET_INTERVAL &&
                Stopwatch.GetElapsedTime(firstDanmuAt, now) < SNAPSHOT_REFRESH_INTERVAL;
        }
    }

    private void ClearLiveQueue()
    {
        lock (liveGate)
        {
            liveDanmu.Clear();
            liveTranslations.Clear();
        }
        activeItems.Clear();
        earlyTranslations.Clear();
    }

    private async Task FlushChangesAsync()
    {
        var current = room;
        bool requiresSnapshot;
        MessageRecord[] appended;
        TranslationCompletedEventArgs[] translated;
        lock (liveGate)
        {
            requiresSnapshot = Interlocked.Exchange(ref refreshRequested, 0) != 0;
            appended = requiresSnapshot ? [] : liveDanmu.ToArray();
            translated = requiresSnapshot ? [] : liveTranslations.ToArray();
            liveDanmu.Clear();
            liveTranslations.Clear();
        }

        if (disposed || current is null)
        {
            return;
        }
        if (requiresSnapshot)
        {
            await RefreshAsync();
            return;
        }
        if (appended.Length == 0 && translated.Length == 0)
        {
            return;
        }

        var version = revision;
        var cancellationToken = lifetime.Token;
        bool applied;
        try
        {
            await refreshGate.WaitAsync(cancellationToken);
            try
            {
                if (!WindowIsCurrent(version, cancellationToken))
                {
                    return;
                }
                applied = await ApplyLiveChangesAsync(current.Id, appended, translated, version, cancellationToken);
            }
            finally
            {
                refreshGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!applied && WindowIsCurrent(version, cancellationToken))
        {
            await RefreshAsync();
        }
    }

    private async Task<bool> ApplyLiveChangesAsync(string roomId, MessageRecord[] appended,
        TranslationCompletedEventArgs[] translated, int version, CancellationToken cancellationToken)
    {
        var context = CaptureProjectionContext();
        if (!ReferenceEquals(context.Display, displaySettings) || context.FilterVersion != displayFilterVersion ||
            !ProjectionOptionsAreCurrent(context))
        {
            return false;
        }

        var updates = new Dictionary<string, (MessageItemViewModel Item, MessageRecord Record)>(StringComparer.Ordinal);
        var newTranslations = new Dictionary<string, TranslationCompletedEventArgs>(StringComparer.Ordinal);
        foreach (var result in translated)
        {
            if (activeItems.TryGetValue(result.MessageId, out var item))
            {
                if (MatchesTranslation(item.Record, result) &&
                    (item.TranslationPending || item.Record.Translation != result.Translation))
                {
                    updates[result.MessageId] = (item, item.Record with { Translation = result.Translation });
                }
            }
            else
            {
                newTranslations[result.MessageId] = result;
            }
        }

        var arriving = appended
            .Where(record => record.RoomTabId == roomId && !activeItems.ContainsKey(record.Id))
            .DistinctBy(record => record.Id, StringComparer.Ordinal)
            .Select(record => (Record: record, Translation: newTranslations.GetValueOrDefault(record.Id) ??
                earlyTranslations.GetValueOrDefault(record.Id))).ToArray();
        if (keepHistoryWindow && IsReadingHistory && updates.Count == 0 && ScrollingItems.Count > 0 && arriving.Length > 0)
        {
            var lastTime = ScrollingItems[^1].Record.Time.ToUnixTimeMilliseconds();
            if (arriving.All(value => value.Record.Time.ToUnixTimeMilliseconds() >= lastTime))
            {
                if (!HasNewerHistory)
                {
                    var hasVisible = await Task.Run(() => arriving.Any(value => IsVisibleLiveDanmu(value.Record, context)),
                        cancellationToken);
                    if (!WindowIsCurrent(version, cancellationToken) || !keepHistoryWindow || !ProjectionOptionsAreCurrent(context))
                    {
                        return false;
                    }
                    if (hasVisible)
                    {
                        MarkNewerDanmu();
                    }
                }
                return true;
            }
        }
        var visibleUpdates = updates.Values.ToArray();
        var (projectedUpdates, incoming) = await Task.Run(() => PrepareLiveRows(arriving, visibleUpdates, context,
            cancellationToken), cancellationToken);
        if (!WindowIsCurrent(version, cancellationToken) || !ProjectionOptionsAreCurrent(context, checkDisplay: true))
        {
            return false;
        }

        if (incoming.Count > 0)
        {
            var lastTime = ScrollingItems.Count > 0 ? ScrollingItems[^1].Record.Time.ToUnixTimeMilliseconds() : long.MinValue;
            foreach (var projection in incoming)
            {
                var time = projection.Record.Time.ToUnixTimeMilliseconds();
                if (time < lastTime)
                {
                    return false;
                }
                lastTime = time;
            }
            if (keepHistoryWindow && ScrollingItems.Count > 0)
            {
                MarkNewerDanmu();
                incoming = [];
            }
        }
        if (incoming.Count > 0 && !CanAppendLiveDanmu(incoming.Count))
        {
            return false;
        }

        if (incoming.Count > 0 || !IsReadingHistory)
        {
            foreach (var result in newTranslations.Values)
            {
                if (earlyTranslations.Count >= retentionCount && !earlyTranslations.ContainsKey(result.MessageId))
                {
                    earlyTranslations.Remove(earlyTranslations.Keys.First());
                }
                earlyTranslations[result.MessageId] = result;
            }
        }
        if (incoming.Count == 0 && projectedUpdates.Length == 0)
        {
            return true;
        }

        BeginViewportUpdate();
        try
        {
            var budget = new FeedUiWorkBudget();
            var added = new MessageItemViewModel[incoming.Count];
            for (var index = 0; index < added.Length; index++)
            {
                if (!WindowIsCurrent(version, cancellationToken) || !ProjectionOptionsAreCurrent(context))
                {
                    return false;
                }
                added[index] = MessageItemViewModel.CreateDeferred(incoming[index], runtime.Pinyin);
                await budget.YieldIfNeededAsync(cancellationToken);
            }
            foreach (var update in projectedUpdates)
            {
                if (!WindowIsCurrent(version, cancellationToken) || !ProjectionOptionsAreCurrent(context))
                {
                    return false;
                }
                update.ExistingItem!.Apply(update.Projection);
                await budget.YieldIfNeededAsync(cancellationToken);
            }
            if (!WindowIsCurrent(version, cancellationToken) || !ProjectionOptionsAreCurrent(context, checkDisplay: true) ||
                added.Length > 0 && !CanAppendLiveDanmu(added.Length))
            {
                return false;
            }
            if (added.Length > 0)
            {
                var removed = Math.Max(0, ScrollingItems.Count + added.Length - retentionCount);
                if (WouldRemoveReadingAnchor(removed))
                {
                    keepHistoryWindow = true;
                    MarkNewerDanmu();
                    added = [];
                }
                else
                {
                    AppendLiveDanmu(added, removed, cancellationToken);
                }
            }
            SchedulePresentation(version, cancellationToken, projectedUpdates.Select(update => update.ExistingItem!).Concat(added));
            return true;
        }
        finally
        {
            EndViewportUpdate();
        }
    }

    private static (FeedRowUpdate[] Updates, IReadOnlyList<MessageRowProjection> Incoming) PrepareLiveRows(
        (MessageRecord Record, TranslationCompletedEventArgs? Translation)[] arriving,
        (MessageItemViewModel Item, MessageRecord Record)[] visibleUpdates, FeedProjectionContext context,
        CancellationToken cancellationToken)
    {
        var display = context.Display;
        var prepared = new FeedRowUpdate[visibleUpdates.Length];
        for (var index = 0; index < visibleUpdates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (item, record) = visibleUpdates[index];
            prepared[index] = new(item, MessageRowProjection.Create(record, display, isTranslationPending: false));
        }
        var incoming = new List<MessageRowProjection>(arriving.Length);
        foreach (var (record, result) in arriving)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsVisibleLiveDanmu(record, context))
            {
                continue;
            }

            var pending = display.TranslationEnabled && record.TranslationFingerprint == display.TranslationFingerprint;
            var projection = result is not null && MatchesTranslation(record, result)
                ? MessageRowProjection.Create(record with { Translation = result.Translation }, display, isTranslationPending: false)
                : MessageRowProjection.Create(record, display, pending);
            incoming.Add(projection);
        }
        return (prepared, incoming);
    }

    private static bool IsVisibleLiveDanmu(MessageRecord record, FeedProjectionContext context)
    {
        return (!context.HideEmoticonDanmu || !TranslationTextExtractor.IsEmoticonOnly(record.Content,
            record.Emoticons.Select(emoticon => emoticon.Text))) && !context.Display.DanmuFilter.IsBlocked(record.Content);
    }

    private bool CanAppendLiveDanmu(int incomingCount)
    {
        var settings = runtime.Configuration.Settings;
        return kind == MessageKind.Danmu && hasLoadedRoom && !HasNewerHistory &&
            !keepHistoryWindow && !IsLoadingHistory && ScrollingRows.Count == ScrollingItems.Count &&
            Items.Count == PinnedItems.Count + ScrollingItems.Count && retentionCount == settings.MessageRetentionCount &&
            incomingCount <= retentionCount;
    }

    private bool WouldRemoveReadingAnchor(int removed)
    {
        if (removed > 0 && GetReadingAnchor() is { } anchor)
        {
            for (var index = 0; index < removed; index++)
            {
                if (ReferenceEquals(ScrollingItems[index], anchor))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void MarkNewerDanmu()
    {
        if (scrollingPage is { HasNewer: false } page)
        {
            SetPages(page with { HasNewer = true }, pinnedPage);
        }
    }

    private void AppendLiveDanmu(IReadOnlyList<MessageItemViewModel> added, int removed, CancellationToken cancellationToken)
    {
        var all = (FeedCollection<MessageItemViewModel>)Items;
        var scrolling = (FeedCollection<MessageItemViewModel>)ScrollingItems;
        var rows = (FeedCollection<FeedRowViewModel>)ScrollingRows;
        for (var index = 0; index < removed; index++)
        {
            activeItems.Remove(scrolling[index].Record.Id);
            presentationQueue.Remove(scrolling[index]);
        }
        foreach (var item in added)
        {
            activeItems.Add(item.Record.Id, item);
            earlyTranslations.Remove(item.Record.Id);
        }
        all.RemoveRange(PinnedItems.Count, removed);
        scrolling.RemoveRange(0, removed);
        rows.RemoveRange(0, removed);
        all.InsertRange(all.Count, added);
        scrolling.InsertRange(scrolling.Count, added);
        rows.InsertRange(rows.Count, added);
        retainedHistory = null;
        if (removed > 0 && scrollingPage is { HasOlder: false } page)
        {
            SetPages(page with { HasOlder = true }, pinnedPage);
        }
        NotifyRegions();
        foreach (var item in added)
        {
            _ = LoadEmoticonsAsync(item, cancellationToken);
        }
    }

    private static bool MatchesTranslation(MessageRecord record, TranslationCompletedEventArgs result)
    {
        return record.RoomTabId == result.RoomTabId && record.Generation == result.Generation &&
            record.TranslationFingerprint == result.ConfigurationFingerprint;
    }
}
