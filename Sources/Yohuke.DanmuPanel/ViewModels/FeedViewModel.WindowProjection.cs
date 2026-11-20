using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private readonly Dictionary<string, MessageItemViewModel> activeItems = new(StringComparer.Ordinal);
    private int displayFilterVersion;
    private int projectionOptionsRevision;

    private FeedProjectionContext CaptureProjectionContext()
    {
        var settings = runtime.Configuration.Settings;
        var display = MessageDisplaySettings.Capture(settings, displaySettings, runtime.DanmuFilter);
        return new(display, display.DanmuFilter.StateVersion, SortOrder, settings.MessageRetentionCount,
            settings.HideEmoticonDanmu, Volatile.Read(ref projectionOptionsRevision));
    }

    private bool ProjectionOptionsAreCurrent(FeedProjectionContext context, bool checkDisplay = false)
    {
        var settings = runtime.Configuration.Settings;
        var filter = runtime.DanmuFilter;
        return context.OptionsRevision == Volatile.Read(ref projectionOptionsRevision) &&
            ReferenceEquals(context.Display.DanmuFilter, filter) && context.FilterVersion == filter.StateVersion &&
            context.SortOrder == settings.MessageSorting.GetOrder(kind, ReadFilter) &&
            context.RetentionCount == settings.MessageRetentionCount && context.HideEmoticonDanmu == settings.HideEmoticonDanmu &&
            (!checkDisplay || ReferenceEquals(context.Display, MessageDisplaySettings.Capture(settings, context.Display, filter)));
    }

    private bool WindowIsCurrent(int version, CancellationToken cancellationToken)
    {
        return !disposed && version == revision && !cancellationToken.IsCancellationRequested;
    }

    private Task<FeedWindowProjection> PlanWindowAsync(MessageFeedWindow window, FeedProjectionContext context,
        CancellationToken cancellationToken)
    {
        var existing = Items.Select(item => new FeedItemState(item, item.Record, item.PresentationSettings,
            item.FilterVersion, item.TranslationPending, item.TranslationBlocked)).ToArray();
        var currentPinned = PinnedItems.ToArray();
        var currentScrolling = ScrollingItems.ToArray();
        var currentRows = ScrollingRows.ToArray();
        return Task.Run(() => PlanWindow(window, context, kind, existing, currentPinned, currentScrolling, currentRows,
            cancellationToken), cancellationToken);
    }

    private static FeedWindowProjection PlanWindow(MessageFeedWindow window, FeedProjectionContext context, MessageKind kind,
        FeedItemState[] existing, MessageItemViewModel[] currentPinned, MessageItemViewModel[] currentScrolling,
        FeedRowViewModel[] currentRows, CancellationToken cancellationToken)
    {
        var existingById = existing.ToDictionary(item => item.Record.Id, StringComparer.Ordinal);
        var display = context.Display;
        var updates = new List<FeedRowUpdate>();
        var candidates = new List<MessageRecord>();
        var pinned = Project(window.Pinned.Entries, reverse: false);
        var scrolling = Project(window.Scrolling.Entries, reverse: context.SortOrder == MessageSortOrder.NewestFirst);
        return new(updates,
            FeedCollectionSynchronizer.Plan<object>(existing.Select(state => state.Item).ToArray(), [.. pinned, .. scrolling], cancellationToken),
            FeedCollectionSynchronizer.Plan(currentPinned, pinned, cancellationToken),
            FeedCollectionSynchronizer.Plan(currentScrolling, scrolling, cancellationToken),
            FeedCollectionSynchronizer.Plan(currentRows, scrolling, cancellationToken, static row => row is MessageLoadingPlaceholderViewModel),
            candidates);

        object[] Project(IReadOnlyList<MessageFeedEntry> entries, bool reverse)
        {
            var result = new object[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = entries[reverse ? entries.Count - index - 1 : index].Record;
                var pending = record.TranslationFingerprint == display.TranslationFingerprint &&
                    window.PendingTranslationIds.Contains(record.Id);
                var hasExisting = existingById.TryGetValue(record.Id, out var previous);
                var blocked = previous.TranslationBlocked;
                if (hasExisting && ReferenceEquals(previous.Record, record) && ReferenceEquals(previous.Display, display) &&
                    previous.FilterVersion == context.FilterVersion && previous.TranslationPending == (pending && !blocked))
                {
                    result[index] = previous.Item;
                }
                else
                {
                    var projection = MessageRowProjection.Create(record, display, pending,
                        isEventSummary: kind == MessageKind.Danmu && record.Kind != MessageKind.Danmu);
                    blocked = projection.TranslationBlocked;
                    result[index] = hasExisting ? previous.Item : projection;
                    updates.Add(new(hasExisting ? previous.Item : null, projection));
                }
                if (!blocked && display.TranslationEnabled && record.Kind is MessageKind.Danmu or MessageKind.SuperChat &&
                    record.TranslationFingerprint != display.TranslationFingerprint)
                {
                    candidates.Add(record);
                }
            }
            return result;
        }
    }

    private async Task<bool> ApplyWindowAsync(FeedWindowProjection plan, FeedProjectionContext context, int version,
        CancellationToken cancellationToken, Func<bool> readingPositionIsCurrent)
    {
        var added = new Dictionary<MessageRowProjection, MessageItemViewModel>(ReferenceEqualityComparer.Instance);
        var budget = new FeedUiWorkBudget();
        foreach (var update in plan.Updates)
        {
            if (!WindowIsCurrent(version, cancellationToken) || !ProjectionOptionsAreCurrent(context))
            {
                return false;
            }
            if (update.ExistingItem is { } item)
            {
                item.Apply(update.Projection);
            }
            else
            {
                added.Add(update.Projection, MessageItemViewModel.CreateDeferred(update.Projection, runtime.Pinyin));
            }
            await budget.YieldIfNeededAsync(cancellationToken);
        }

        if (!WindowIsCurrent(version, cancellationToken) || !ProjectionOptionsAreCurrent(context, checkDisplay: true) ||
            !readingPositionIsCurrent())
        {
            return false;
        }

        displaySettings = context.Display;
        displayFilterVersion = context.FilterVersion;
        FeedCollectionSynchronizer.ApplyEdits(Items, plan.Items, ResolveItem);
        FeedCollectionSynchronizer.ApplyEdits(PinnedItems, plan.Pinned, ResolveItem);
        FeedCollectionSynchronizer.ApplyEdits(ScrollingItems, plan.Scrolling, ResolveItem);
        FeedCollectionSynchronizer.ApplyEdits(ScrollingRows, plan.Rows, token => ResolveItem(token));
        if (plan.Items.Count > 0)
        {
            activeItems.Clear();
            foreach (var item in Items)
            {
                activeItems.Add(item.Record.Id, item);
            }
            presentationQueue.RemoveInactive(ContainsActiveItem);
        }

        var roomToken = lifetime.Token;
        foreach (var item in added.Values)
        {
            _ = LoadEmoticonsAsync(item, roomToken);
        }
        SchedulePresentation(version, roomToken);
        var fingerprint = context.Display.TranslationFingerprint;
        var requested = plan.TranslationCandidates.Where(record => translationRequested.Add(record.Id + fingerprint)).ToArray();
        TrackBackground(RegisterTranslationsAsync(requested, fingerprint, version, roomToken));
        return true;

        MessageItemViewModel ResolveItem(object token)
        {
            return token is MessageItemViewModel item ? item : added[(MessageRowProjection)token];
        }
    }

    private bool ContainsActiveItem(MessageItemViewModel item)
    {
        return activeItems.TryGetValue(item.Record.Id, out var active) && ReferenceEquals(active, item);
    }
}
