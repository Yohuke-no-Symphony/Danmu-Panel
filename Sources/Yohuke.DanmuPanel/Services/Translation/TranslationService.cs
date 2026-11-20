using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>为相同翻译请求提供缓存及进行中请求合并。</summary>
public sealed class TranslationService : IAsyncDisposable
{
    private readonly ITranslationClient client;
    private readonly MemoryTranslationCache cache;
    private readonly Dictionary<string, SharedTranslationRequest> inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<SharedTranslationRequest> active = [];
    private readonly object gate = new();
    private Task? disposal;

    /// <summary>使用可调整容量和有效期的内存缓存。</summary>
    public TranslationService(ITranslationClient client, int cacheCapacity = TranslationSettings.DEFAULT_CACHE_CAPACITY,
        int cacheTtlMinutes = TranslationSettings.DEFAULT_CACHE_TTL_MINUTES, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        cache = new(cacheCapacity, cacheTtlMinutes, timeProvider ?? TimeProvider.System);
    }

    /// <summary>共享一次模型请求；所有等待者取消后才取消该请求。</summary>
    public Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        return TranslateCoreAsync(request, false, cancellationToken);
    }

    /// <summary>跳过缓存及旧在途请求，独立请求模型并更新缓存，使旧请求无法覆盖新结果。</summary>
    public Task<string> RefreshAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        return TranslateCoreAsync(request, true, cancellationToken);
    }

    internal void ConfigureCache(int cacheCapacity, int cacheTtlMinutes)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            cache.Configure(cacheCapacity, cacheTtlMinutes);
        }
    }

    internal void ClearCache()
    {
        lock (gate)
        {
            cache.Clear();
            inFlight.Clear();
        }
    }

    internal TranslationCacheEntry[] CaptureCacheSnapshot()
    {
        lock (gate)
        {
            return cache.CaptureSnapshot();
        }
    }

    internal void RestoreCache(IEnumerable<TranslationCacheEntry> entries, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            cache.Restore(entries);
        }
    }

    private async Task<string> TranslateCoreAsync(TranslationRequest request, bool forceRefresh, CancellationToken cancellationToken)
    {
        SharedTranslationRequest shared;
        bool startRequest;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposal is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!forceRefresh && cache.Get(request.CacheKey) is { } cached)
            {
                return cached;
            }

            inFlight.TryGetValue(request.CacheKey, out var previous);
            if (forceRefresh || previous is null || previous.Request != request)
            {
                shared = new(request);
                inFlight[request.CacheKey] = shared;
                active.Add(shared);
                startRequest = true;
            }
            else
            {
                shared = previous;
                startRequest = false;
            }
            shared.Waiters++;
        }
        if (startRequest)
        {
            _ = ExecuteAsync(shared);
        }

        try
        {
            return await shared.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWaiter(shared);
        }
    }

    /// <summary>取消仍在执行的共享请求并等待结束。</summary>
    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is null)
            {
                foreach (var shared in active)
                {
                    if (!shared.Finished)
                    {
                        shared.CancellationTask ??= shared.Cancellation.CancelAsync();
                    }
                }
                disposal = ObserveCompletionAsync(Task.WhenAll(active.Select(shared => shared.Completion.Task)));
            }
            return new(disposal);
        }
    }

    private async Task ExecuteAsync(SharedTranslationRequest shared)
    {
        var cancellationToken = shared.Cancellation.Token;
        string? translation = null;
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            translation = await client.TranslateAsync(shared.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Task? cancellation;
        lock (gate)
        {
            cancellation = shared.CancellationTask;
            if (RemoveCurrentRequest(shared) && cancellation is null && failure is null)
            {
                try
                {
                    cache.Set(shared.Request.CacheKey, translation!);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }
            shared.Finished = true;
        }

        if (cancellation is not null)
        {
            await ObserveCompletionAsync(cancellation).ConfigureAwait(false);
        }
        shared.Cancellation.Dispose();
        lock (gate)
        {
            active.Remove(shared);
            if (cancellation is not null)
            {
                shared.Completion.SetCanceled(cancellationToken);
            }
            else if (failure is OperationCanceledException cancelled)
            {
                shared.Completion.SetCanceled(cancelled.CancellationToken);
            }
            else if (failure is not null)
            {
                shared.Completion.SetException(failure);
                _ = shared.Completion.Task.Exception;
            }
            else
            {
                shared.Completion.SetResult(translation!);
            }
        }
    }

    private void ReleaseWaiter(SharedTranslationRequest shared)
    {
        lock (gate)
        {
            if (--shared.Waiters == 0 && !shared.Finished)
            {
                RemoveCurrentRequest(shared);
                shared.CancellationTask ??= shared.Cancellation.CancelAsync();
            }
        }
    }

    private bool RemoveCurrentRequest(SharedTranslationRequest shared)
    {
        var key = shared.Request.CacheKey;
        return inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, shared) && inFlight.Remove(key);
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }
}
