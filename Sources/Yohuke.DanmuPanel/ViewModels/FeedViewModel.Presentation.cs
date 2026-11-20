using Avalonia.Threading;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private FeedPresentationQueue presentationQueue = new();
    private readonly HashSet<Task> backgroundTasks = [];
    internal Func<MessageItemViewModel, bool>? IsVisibleForPresentation { get; set; }

    internal async Task WaitForPresentationAsync()
    {
        while (!disposed)
        {
            await refreshGate.WaitAsync(lifetime.Token);
            refreshGate.Release();
            if (backgroundTasks.Count == 0)
            {
                return;
            }
            await Task.WhenAll(backgroundTasks.ToArray());
            backgroundTasks.RemoveWhere(task => task.IsCompleted);
        }
    }

    internal void PrioritizePresentation(MessageItemViewModel item)
    {
        if (!disposed && item.IsPresentationPending && ContainsActiveItem(item))
        {
            presentationQueue.Enqueue(item, visible: true);
            StartPresentationIfNeeded(revision, lifetime.Token);
        }
    }

    internal void DeprioritizePresentation(MessageItemViewModel item)
    {
        if (item.IsPresentationPending && IsVisibleForPresentation?.Invoke(item) != true)
        {
            presentationQueue.Deprioritize(item);
        }
    }

    private void SchedulePresentation(int version, CancellationToken cancellationToken,
        IEnumerable<MessageItemViewModel>? candidates = null)
    {
        var queue = presentationQueue;
        foreach (var item in candidates ?? Items)
        {
            if (item.IsPresentationPending)
            {
                queue.Enqueue(item, IsVisibleForPresentation?.Invoke(item) == true);
            }
        }
        StartPresentationIfNeeded(version, cancellationToken);
    }

    private void StartPresentationIfNeeded(int version, CancellationToken cancellationToken)
    {
        var queue = presentationQueue;
        if (queue.HasWaiting && !queue.IsRunning)
        {
            queue.IsRunning = true;
            TrackBackground(PreparePresentationAsync(queue, version, cancellationToken));
        }
    }

    private async Task PreparePresentationAsync(FeedPresentationQueue queue, int version, CancellationToken cancellationToken)
    {
        var inFlight = new Dictionary<Task<MessagePresentationSnapshot>, MessageItemViewModel>();
        try
        {
            BeginViewportUpdate();
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, cancellationToken);
            var budget = new FeedUiWorkBudget();
            while (WindowIsCurrent(version, cancellationToken))
            {
                while (inFlight.Count < runtime.Presentations.WorkerCount && queue.Take() is { } next)
                {
                    if (!ContainsActiveItem(next) || !next.IsPresentationPending)
                    {
                        queue.Remove(next);
                        continue;
                    }
                    var work = runtime.Presentations.PrepareAsync(next.Record,
                        next.PresentationSettings.PresentationOptions, cancellationToken, next.Emoticons);
                    inFlight.Add(work, next);
                }
                if (inFlight.Count == 0)
                {
                    break;
                }

                Task<MessagePresentationSnapshot>? completed = null;
                foreach (var task in inFlight.Keys)
                {
                    if (task.IsCompleted)
                    {
                        completed = task;
                        break;
                    }
                }
                completed ??= await Task.WhenAny(inFlight.Keys);
                var item = inFlight[completed];
                inFlight.Remove(completed);
                try
                {
                    var prepared = await completed;
                    if (!WindowIsCurrent(version, cancellationToken))
                    {
                        return;
                    }
                    queue.Remove(item);
                    if (ContainsActiveItem(item) && item.IsPresentationPending && !item.ApplyPresentation(prepared))
                    {
                        queue.Enqueue(item, IsVisibleForPresentation?.Invoke(item) == true);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    queue.Remove(item);
                    if (WindowIsCurrent(version, cancellationToken) && ContainsActiveItem(item))
                    {
                        item.FailPresentation();
                        Failed?.Invoke(error);
                    }
                }
                await budget.YieldIfNeededAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (WindowIsCurrent(version, cancellationToken))
            {
                while (queue.Take() is { } item)
                {
                    queue.Remove(item);
                    item.FailPresentation();
                }
                foreach (var item in inFlight.Values)
                {
                    queue.Remove(item);
                    if (ContainsActiveItem(item))
                    {
                        item.FailPresentation();
                    }
                }
                Failed?.Invoke(error);
            }
        }
        finally
        {
            try
            {
                if (inFlight.Count != 0)
                {
                    await Task.WhenAll(inFlight.Keys);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (WindowIsCurrent(version, cancellationToken))
                {
                    Failed?.Invoke(error);
                }
            }
            finally
            {
                queue.IsRunning = false;
                EndViewportUpdate();
            }
        }
    }

    private async Task RegisterTranslationsAsync(IReadOnlyList<MessageRecord> records, string fingerprint, int version,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }
        try
        {
            foreach (var batch in records.Chunk(IMessageStore.MAX_BATCH_COUNT))
            {
                if (disposed || version != revision || cancellationToken.IsCancellationRequested ||
                    displaySettings?.TranslationFingerprint != fingerprint || !displaySettings.TranslationEnabled)
                {
                    return;
                }
                var queued = await runtime.Messages.UpsertTranslationJobsAsync(batch, fingerprint, cancellationToken);
                if (disposed || version != revision || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (displaySettings?.TranslationFingerprint != fingerprint || !displaySettings.TranslationEnabled)
                {
                    return;
                }
                foreach (var record in batch)
                {
                    if (queued.Contains(record.Id) && activeItems.TryGetValue(record.Id, out var item) &&
                        record.Generation == item.Record.Generation)
                    {
                        item.SetTranslationPending(true);
                    }
                }
                if (queued.Count > 0)
                {
                    runtime.NotifyTranslation();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!disposed && version == revision)
            {
                Failed?.Invoke(error);
            }
        }
        finally
        {
            if (version == revision)
            {
                foreach (var record in records)
                {
                    translationRequested.Remove(record.Id + fingerprint);
                }
            }
        }
    }

    private void TrackBackground(Task task)
    {
        if (!task.IsCompleted)
        {
            backgroundTasks.Add(task);
            _ = ObserveBackgroundAsync(task);
        }
    }

    private async Task ObserveBackgroundAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            backgroundTasks.Remove(task);
        }
    }
}
