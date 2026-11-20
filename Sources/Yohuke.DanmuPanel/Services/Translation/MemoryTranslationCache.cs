using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

internal sealed class MemoryTranslationCache
{
    private readonly Dictionary<string, LinkedListNode<TranslationCacheEntry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<TranslationCacheEntry> recency = new();
    private readonly TimeProvider timeProvider;
    private int capacity;
    private TimeSpan ttl;

    internal MemoryTranslationCache(int capacity, int ttlMinutes, TimeProvider timeProvider)
    {
        ValidateOptions(capacity, ttlMinutes);
        this.capacity = capacity;
        this.timeProvider = timeProvider;
        ttl = TimeSpan.FromMinutes(ttlMinutes);
    }

    internal string? Get(string key)
    {
        if (!entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        if (timeProvider.GetUtcNow() - entry.Value.StoredAtUtc >= ttl)
        {
            Remove(entry);
            return null;
        }

        MarkUsed(entry);
        return entry.Value.Translation;
    }

    internal void Set(string key, string translation)
    {
        if (capacity != 0)
        {
            Set(new(key, translation, timeProvider.GetUtcNow()));
        }
    }

    internal TranslationCacheEntry[] CaptureSnapshot()
    {
        var now = timeProvider.GetUtcNow();
        return recency.Where(entry => IsFresh(entry, now)).ToArray();
    }

    internal void Restore(IEnumerable<TranslationCacheEntry> snapshot)
    {
        Clear();
        if (capacity == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        foreach (var entry in snapshot)
        {
            if (IsFresh(entry, now))
            {
                Set(entry);
            }
        }
    }

    internal void Configure(int capacity, int ttlMinutes)
    {
        ValidateOptions(capacity, ttlMinutes);
        var nextTtl = TimeSpan.FromMinutes(ttlMinutes);
        if (this.capacity == capacity && ttl == nextTtl)
        {
            return;
        }

        if (capacity == 0)
        {
            Clear();
        }
        else
        {
            PruneExpired(nextTtl < ttl ? nextTtl : ttl);
        }
        this.capacity = capacity;
        ttl = nextTtl;
        TrimToCapacity();
    }

    internal void Clear()
    {
        entries.Clear();
        recency.Clear();
    }

    private void Set(TranslationCacheEntry value)
    {
        if (entries.TryGetValue(value.Key, out var entry))
        {
            entry.Value = value;
            MarkUsed(entry);
        }
        else
        {
            entries.Add(value.Key, recency.AddLast(value));
        }
        TrimToCapacity();
    }

    private bool IsFresh(TranslationCacheEntry entry, DateTimeOffset now)
    {
        return entry.StoredAtUtc <= now && now - entry.StoredAtUtc < ttl;
    }

    private void MarkUsed(LinkedListNode<TranslationCacheEntry> entry)
    {
        if (entry != recency.Last)
        {
            recency.Remove(entry);
            recency.AddLast(entry);
        }
    }

    private void PruneExpired(TimeSpan maximumAge)
    {
        var now = timeProvider.GetUtcNow();
        var entry = recency.First;
        while (entry is not null)
        {
            var next = entry.Next;
            if (now - entry.Value.StoredAtUtc >= maximumAge)
            {
                Remove(entry);
            }
            entry = next;
        }
    }

    private void TrimToCapacity()
    {
        while (entries.Count > capacity)
        {
            Remove(recency.First!);
        }
    }

    private void Remove(LinkedListNode<TranslationCacheEntry> entry)
    {
        entries.Remove(entry.Value.Key);
        recency.Remove(entry);
    }

    private static void ValidateOptions(int capacity, int ttlMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, TranslationSettings.MAX_CACHE_CAPACITY);
        ArgumentOutOfRangeException.ThrowIfLessThan(ttlMinutes, TranslationSettings.MIN_CACHE_TTL_MINUTES);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ttlMinutes, TranslationSettings.MAX_CACHE_TTL_MINUTES);
    }
}
