using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private const int MAX_DIRECT_DANMU_BATCH = 128;
    private readonly object liveGate = new();
    private readonly Dictionary<string, MessageItemViewModel> activeDanmuItems = new(StringComparer.Ordinal);
    private readonly Queue<MessageRecord> liveDanmu = new();
    private readonly Queue<TranslationCompletedEventArgs> liveTranslations = new();
    private readonly Dictionary<string, TranslationCompletedEventArgs> earlyTranslations = new(StringComparer.Ordinal);

    private void OnDanmuCommitted(MessageRecord message)
    {
        lock (liveGate)
        {
            if (Volatile.Read(ref disposed) || Volatile.Read(ref room)?.Id != message.RoomTabId)
            {
                return;
            }
            if (liveDanmu.Count >= Math.Min(MAX_DIRECT_DANMU_BATCH, Volatile.Read(ref retentionCount)))
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

    private void ClearLiveQueue()
    {
        lock (liveGate)
        {
            liveDanmu.Clear();
            liveTranslations.Clear();
        }
        activeDanmuItems.Clear();
        earlyTranslations.Clear();
    }

    private async Task FlushChangesAsync(bool requiresSnapshot)
    {
        var current = room;
        if (disposed || current is null)
        {
            return;
        }

        MessageRecord[] appended;
        TranslationCompletedEventArgs[] translated;
        lock (liveGate)
        {
            appended = liveDanmu.ToArray();
            translated = liveTranslations.ToArray();
            liveDanmu.Clear();
            liveTranslations.Clear();
        }
        var version = revision;
        var cancellationToken = lifetime.Token;
        if (!requiresSnapshot && appended.Length > 0)
        {
            var filter = runtime.DanmuFilter;
            var hideEmoticons = runtime.Configuration.Settings.HideEmoticonDanmu;
            if (filter.Enabled || hideEmoticons)
            {
                var filterVersion = filter.StateVersion;
                var records = appended;
                try
                {
                    appended = await Task.Run(() => Array.FindAll(records, record =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return (!hideEmoticons || !TranslationTextExtractor.IsEmoticonOnly(record.Content,
                            record.Emoticons.Select(emoticon => emoticon.Text))) && !filter.IsBlocked(record.Content);
                    }), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (disposed || version != revision)
                {
                    return;
                }
                requiresSnapshot = !ReferenceEquals(filter, runtime.DanmuFilter) ||
                    !ReferenceEquals(filter, displaySettings?.DanmuFilter) || filter.StateVersion != filterVersion ||
                    displayFilterVersion != filterVersion || hideEmoticons != runtime.Configuration.Settings.HideEmoticonDanmu;
            }
        }
        if (!requiresSnapshot && keepHistoryWindow && scrollingPage is { Entries.Count: > 0 } page &&
            Array.TrueForAll(appended, record => record.Time.ToUnixTimeMilliseconds() > page.Entries[^1].Cursor.OccurredMilliseconds))
        {
            if (appended.Length > 0 && !page.HasNewer)
            {
                SetPages(page with { HasNewer = true }, pinnedPage);
            }
            appended = [];
        }
        if (requiresSnapshot || appended.Length > 0 && !CanAppendLiveDanmu())
        {
            await RefreshAsync();
            if (version != revision)
            {
                return;
            }
            appended = [];
        }

        if (appended.Length == 0 && translated.Length == 0)
        {
            return;
        }
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
            if (disposed || version != revision || displaySettings is not { } display)
            {
                return;
            }
            if (appended.Length > 0 && !CanAppendLiveDanmu())
            {
                Interlocked.Exchange(ref refreshRequested, 1);
                return;
            }

            var incoming = appended.Where(record => record.RoomTabId == current.Id && !activeDanmuItems.ContainsKey(record.Id))
                .TakeLast(retentionCount).ToArray();
            if (incoming.Length == 0 && translated.Length == 0)
            {
                return;
            }
            BeginViewportUpdate();
            try
            {
                foreach (var result in translated)
                {
                    if (activeDanmuItems.TryGetValue(result.MessageId, out var item))
                    {
                        ApplyLiveTranslation(item, result, display);
                    }
                    else if (incoming.Length > 0 || !IsReadingHistory)
                    {
                        if (earlyTranslations.Count >= retentionCount && !earlyTranslations.ContainsKey(result.MessageId))
                        {
                            earlyTranslations.Remove(earlyTranslations.Keys.First());
                        }
                        earlyTranslations[result.MessageId] = result;
                    }
                }
                if (incoming.Length > 0)
                {
                    AppendLiveDanmu(incoming, display, cancellationToken);
                }
                SchedulePresentation(version, cancellationToken);
            }
            finally
            {
                EndViewportUpdate();
            }
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private bool CanAppendLiveDanmu()
    {
        var settings = runtime.Configuration.Settings;
        return kind == MessageKind.Danmu && hasLoadedRoom && !HasNewerHistory &&
            !keepHistoryWindow && !IsLoadingHistory && ScrollingRows.Count == ScrollingItems.Count &&
            Items.Count == PinnedItems.Count + ScrollingItems.Count && retentionCount == settings.MessageRetentionCount;
    }

    private void AppendLiveDanmu(IReadOnlyList<MessageRecord> records, MessageDisplaySettings display, CancellationToken cancellationToken)
    {
        var added = new MessageItemViewModel[records.Count];
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var pending = display.TranslationEnabled && record.TranslationFingerprint == display.TranslationFingerprint &&
                !display.IsTranslationBlocked(record);
            var item = MessageItemViewModel.CreateDeferred(record, display, runtime.Pinyin, pending);
            if (earlyTranslations.Remove(record.Id, out var translation))
            {
                ApplyLiveTranslation(item, translation, display);
            }
            added[index] = item;
        }

        var removed = Math.Max(0, ScrollingItems.Count + added.Length - retentionCount);
        var all = (FeedCollection<MessageItemViewModel>)Items;
        var scrolling = (FeedCollection<MessageItemViewModel>)ScrollingItems;
        var rows = (FeedCollection<FeedRowViewModel>)ScrollingRows;
        for (var index = 0; index < removed; index++)
        {
            activeDanmuItems.Remove(scrolling[index].Record.Id);
        }
        foreach (var item in added)
        {
            activeDanmuItems.Add(item.Record.Id, item);
        }
        all.RemoveRange(PinnedItems.Count, removed);
        scrolling.RemoveRange(0, removed);
        rows.RemoveRange(0, removed);
        all.InsertRange(all.Count, added);
        scrolling.InsertRange(scrolling.Count, added);
        rows.InsertRange(rows.Count, added);
        retainedHistory = null;
        NotifyRegions();
        foreach (var item in added)
        {
            _ = LoadEmoticonsAsync(item, cancellationToken);
        }
    }

    private void ResetActiveDanmuItems()
    {
        if (kind != MessageKind.Danmu)
        {
            return;
        }
        activeDanmuItems.Clear();
        foreach (var item in Items)
        {
            activeDanmuItems[item.Record.Id] = item;
        }
    }

    private static void ApplyLiveTranslation(MessageItemViewModel item, TranslationCompletedEventArgs result, MessageDisplaySettings display)
    {
        var record = item.Record;
        if (record.Generation == result.Generation && record.TranslationFingerprint == result.ConfigurationFingerprint &&
            (item.TranslationPending || record.Translation != result.Translation))
        {
            item.Apply(record with { Translation = result.Translation }, display, isTranslationPending: false);
        }
    }
}
