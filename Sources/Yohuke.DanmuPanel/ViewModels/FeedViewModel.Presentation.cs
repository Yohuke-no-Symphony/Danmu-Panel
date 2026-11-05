using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private readonly HashSet<MessageItemViewModel> preparingItems = [];
    private readonly HashSet<Task> backgroundTasks = [];

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

    private void SchedulePresentation(int version, CancellationToken cancellationToken)
    {
        var pending = Items.Where(item => item.IsPresentationPending && preparingItems.Add(item)).ToArray();
        if (pending.Length > 0)
        {
            TrackBackground(PreparePresentationAsync(pending, version, cancellationToken));
        }
    }

    private async Task PreparePresentationAsync(IReadOnlyList<MessageItemViewModel> pending, int version,
        CancellationToken cancellationToken)
    {
        var inFlight = new Dictionary<Task<MessagePresentationSnapshot>, MessageItemViewModel>();
        try
        {
            BeginViewportUpdate();
            var uiBudget = new FeedUiWorkBudget();
            var next = 0;
            while (next < pending.Count || inFlight.Count > 0)
            {
                if (disposed || version != revision || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                while (next < pending.Count && inFlight.Count < runtime.Presentations.WorkerCount)
                {
                    var item = pending[next++];
                    if (!Items.Contains(item) || !item.IsPresentationPending)
                    {
                        preparingItems.Remove(item);
                        continue;
                    }
                    inFlight.Add(runtime.Presentations.PrepareAsync(item.Record,
                        item.PresentationSettings.PresentationOptions, cancellationToken), item);
                }
                if (inFlight.Count == 0)
                {
                    continue;
                }
                var completed = await Task.WhenAny(inFlight.Keys);
                var completedItem = inFlight[completed];
                inFlight.Remove(completed);
                try
                {
                    var prepared = await completed;
                    if (disposed || version != revision || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    if (Items.Contains(completedItem) && completedItem.IsPresentationPending &&
                        !completedItem.ApplyPresentation(prepared))
                    {
                        inFlight.Add(runtime.Presentations.PrepareAsync(completedItem.Record,
                            completedItem.PresentationSettings.PresentationOptions, cancellationToken), completedItem);
                    }
                    else
                    {
                        preparingItems.Remove(completedItem);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    completedItem.FailPresentation();
                    preparingItems.Remove(completedItem);
                    Failed?.Invoke(error);
                }
                await uiBudget.YieldIfNeededAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!disposed && version == revision)
            {
                foreach (var item in pending)
                {
                    item.FailPresentation();
                }
                Failed?.Invoke(error);
            }
        }
        finally
        {
            try
            {
                try
                {
                    await Task.WhenAll(inFlight.Keys);
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
            }
            finally
            {
                foreach (var item in pending)
                {
                    preparingItems.Remove(item);
                }
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
                foreach (var item in Items)
                {
                    if (queued.Contains(item.Record.Id) && batch.Any(record => record.Id == item.Record.Id &&
                            record.Generation == item.Record.Generation))
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
