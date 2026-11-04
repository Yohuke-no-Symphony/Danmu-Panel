using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

internal sealed class MemoryTranslationJobStore : ITranslationJobStore
{
    private readonly object gate = new();
    internal List<TranslationJob> Pending { get; } = [];
    internal Dictionary<string, string> Results { get; } = [];
    internal int Skipped { get; private set; }

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
        lock (gate)
        {
            return Task.FromResult(Pending.RemoveAll(current => current.Id == job.Id && current.ConfigurationFingerprint == job.ConfigurationFingerprint) > 0);
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

    public Task<int> TrimPendingAsync(DateTimeOffset olderThan, int maxOrdinary, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var remove = Pending.Where(job => job.Priority == 0).OrderByDescending(job => job.CreatedAt)
                .Where((job, index) => job.CreatedAt < olderThan || index >= maxOrdinary).ToHashSet();
            Pending.RemoveAll(remove.Contains);
            Skipped += remove.Count;
            return Task.FromResult(remove.Count);
        }
    }
}
