using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Tests.Translation;

internal sealed class MemoryTranslationJobStore : ITranslationJobStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, TranslationJob> regexBlocked = [];
    internal List<TranslationJob> Pending { get; } = [];
    internal Dictionary<string, string> Results { get; } = [];
    internal int Skipped { get; private set; }
    internal int RegexBlockedCount
    {
        get
        {
            lock (gate)
            {
                return regexBlocked.Count;
            }
        }
    }

    public Task<IReadOnlyList<TranslationJob>> GetPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<TranslationJob>>(Pending.OrderByDescending(job => job.Priority).ThenBy(job => job.CreatedAt).Take(limit).ToArray());
        }
    }

    public Task<bool> CompleteAsync(TranslationJob job, string translation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var index = Pending.FindIndex(current => current.Id == job.Id && current.ConfigurationFingerprint == job.ConfigurationFingerprint);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Results[job.Id] = translation;
            Pending.RemoveAt(index);
            return Task.FromResult(true);
        }
    }

    public Task<bool> FailAsync(TranslationJob job, string error, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var removed = Pending.RemoveAll(current => current.Id == job.Id &&
                current.ConfigurationFingerprint == job.ConfigurationFingerprint) > 0;
            if (removed && error == "skipped:regex")
            {
                regexBlocked[job.Id] = job;
            }
            return Task.FromResult(removed);
        }
    }

    public Task<int> RestoreRegexBlockedAsync(DanmuRegexFilter filter, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            var restored = regexBlocked.Values.Where(job => job.Kind != MessageKind.Danmu ||
                !filter.IsTranslationBlocked(job.Text)).ToArray();
            foreach (var job in restored)
            {
                Pending.Add(job);
                regexBlocked.Remove(job.Id);
            }
            return Task.FromResult(restored.Length);
        }
    }

    public Task ResetPendingAsync(string configurationFingerprint, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            for (var index = 0; index < Pending.Count; index++)
            {
                Pending[index] = Pending[index] with { ConfigurationFingerprint = configurationFingerprint };
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> TrimPendingAsync(DateTimeOffset olderThan, int maxOrdinary, IReadOnlyCollection<string> inFlightJobIds,
        CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var remove = Pending.Where(job => job.Priority == 0 && !inFlightJobIds.Contains(job.Id))
                .OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id, StringComparer.Ordinal)
                .Where((job, index) => job.CreatedAt < olderThan || index >= maxOrdinary).ToHashSet();
            Pending.RemoveAll(remove.Contains);
            Skipped += remove.Count;
            return Task.FromResult(remove.Count);
        }
    }
}
