using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>为相同翻译请求提供缓存及进行中请求合并。</summary>
public sealed class TranslationService(ITranslationClient client, ITranslationCache cache) : IAsyncDisposable
{
    private readonly Dictionary<string, SharedTranslationRequest> inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<SharedTranslationRequest> active = [];
    private readonly object gate = new();
    private readonly SemaphoreSlim cacheWriteGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();

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

    private async Task<string> TranslateCoreAsync(TranslationRequest request, bool forceRefresh, CancellationToken cancellationToken)
    {
        if (!forceRefresh)
        {
            var cached = await cache.GetAsync(request.CacheKey, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        SharedTranslationRequest shared;
        Task<string> task;
        lock (gate)
        {
            inFlight.TryGetValue(request.CacheKey, out var previous);
            if (forceRefresh || previous is null || previous.Request != request)
            {
                shared = new(request, lifetime.Token);
                inFlight[request.CacheKey] = shared;
                active.Add(shared);
            }
            else
            {
                shared = previous;
            }

            shared.Waiters++;
            if (shared.Task is null)
            {
                shared.Task = ExecuteAsync(request, shared, forceRefresh);
                _ = shared.Task.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    lock (gate)
                    {
                        shared.Finished = true;
                        if (shared.Waiters == 0 && !shared.Cancelling)
                        {
                            shared.Cancellation.Dispose();
                        }
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            task = shared.Task;
        }

        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Task? cancellation = null;
            lock (gate)
            {
                shared.Waiters--;
                if (shared.Waiters == 0 && !shared.Finished)
                {
                    if (inFlight.TryGetValue(request.CacheKey, out var current) && ReferenceEquals(current, shared))
                    {
                        inFlight.Remove(request.CacheKey);
                    }

                    shared.Cancelling = true;
                    cancellation = shared.Cancellation.CancelAsync();
                }
                else if (shared.Waiters == 0 && shared.Finished)
                {
                    shared.Cancellation.Dispose();
                }
            }

            if (cancellation is not null)
            {
                await cancellation.ConfigureAwait(false);
                lock (gate)
                {
                    shared.Cancelling = false;
                    if (shared.Finished)
                    {
                        shared.Cancellation.Dispose();
                    }
                }
            }
        }
    }

    /// <summary>取消仍在执行的共享请求并等待结束。</summary>
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (gate)
        {
            pending = [.. active.Select(item => item.Task!)];
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        lifetime.Dispose();
        cacheWriteGate.Dispose();
    }

    private async Task<string> ExecuteAsync(TranslationRequest request, SharedTranslationRequest shared, bool forceRefresh)
    {
        try
        {
            if (!forceRefresh)
            {
                var cached = await cache.GetAsync(request.CacheKey, shared.Cancellation.Token).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
                }
            }

            var result = await client.TranslateAsync(request, shared.Cancellation.Token).ConfigureAwait(false);
            await cacheWriteGate.WaitAsync(shared.Cancellation.Token).ConfigureAwait(false);
            try
            {
                bool currentRequest;
                lock (gate)
                {
                    currentRequest = inFlight.TryGetValue(request.CacheKey, out var current) && ReferenceEquals(current, shared);
                }
                if (currentRequest)
                {
                    await cache.SetAsync(request.CacheKey, result, shared.Cancellation.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                cacheWriteGate.Release();
            }
            return result;
        }
        finally
        {
            lock (gate)
            {
                active.Remove(shared);
                if (inFlight.TryGetValue(request.CacheKey, out var current) && ReferenceEquals(current, shared))
                {
                    inFlight.Remove(request.CacheKey);
                }
            }
        }
    }
}
