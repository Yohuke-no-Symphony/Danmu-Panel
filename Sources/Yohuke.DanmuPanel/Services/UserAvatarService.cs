using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Services;

/// <summary>按 UID 共享头像，持久保存资料并在缓存过期时后台更新。</summary>
public sealed class UserAvatarService : IUserAvatarService, IDisposable, IAsyncDisposable
{
    private const int MAX_CACHED_USERS = 4096;
    private static readonly TimeSpan FRESH_DURATION = TimeSpan.FromHours(6);
    private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromMinutes(5);
    private readonly AvatarCacheService images;
    private readonly IUserAvatarSource source;
    private readonly TimeProvider timeProvider;
    private readonly string metadataDirectory;
    private readonly object syncRoot = new();
    private readonly Dictionary<long, Task<UserAvatarCacheEntry>> entries = [];
    private readonly Dictionary<long, Task<string>> refreshes = [];
    private readonly Dictionary<long, int> callers = [];
    private readonly HashSet<long> loadingUsers = [];
    private readonly LinkedList<long> recency = [];
    private readonly Dictionary<long, LinkedListNode<long>> recencyNodes = [];
    private readonly int maxCachedUsers;
    private readonly SemaphoreSlim querySlots = new(4, 4);
    private readonly CancellationTokenSource lifetime = new();
    private Task? disposal;
    private bool isDisposed;

    /// <inheritdoc />
    public event Action<long>? AvatarChanged;

    internal int CachedUserCount
    {
        get
        {
            lock (syncRoot)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>借用图片下载器并接管用户资料源；可注入时钟验证刷新与重试周期。</summary>
    public UserAvatarService(AppDirectoryService directoryService, AvatarCacheService images,
        IUserAvatarSource? source = null, TimeProvider? timeProvider = null)
        : this(directoryService, images, source, timeProvider, MAX_CACHED_USERS)
    {
    }

    internal UserAvatarService(AppDirectoryService directoryService, AvatarCacheService images,
        IUserAvatarSource? source, TimeProvider? timeProvider, int maxCachedUsers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCachedUsers);
        this.images = images;
        this.source = source ?? new BilibiliUserAvatarSource();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maxCachedUsers = maxCachedUsers;
        metadataDirectory = Path.Combine(directoryService.AvatarCacheDirectory, "Users");
    }

    /// <inheritdoc />
    public async Task<string> GetAvatarAsync(long userUid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userUid <= 0)
        {
            return "";
        }
        var loading = BorrowEntry(userUid);
        try
        {
            await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task<string> refreshing;
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                var entry = entries[userUid].GetAwaiter().GetResult();
                var path = GetCachedPath(userUid, entry);
                var now = timeProvider.GetUtcNow();
                if (path.Length > 0 && entry.LastCheckSucceeded && entry.LastCheckedAt <= now &&
                    IsWithin(now, entry.LastSuccessAt, FRESH_DURATION) ||
                    !entry.LastCheckSucceeded && IsWithin(now, entry.LastCheckedAt, RETRY_DELAY))
                {
                    return path;
                }
                if (!refreshes.TryGetValue(userUid, out refreshing!))
                {
                    refreshing = Task.Run(() => RefreshAndRemoveAsync(userUid, entry, path));
                    refreshes.Add(userUid, refreshing);
                }
                if (path.Length > 0)
                {
                    return path;
                }
            }
            return await refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseEntry(userUid);
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAvatarAsync(long userUid, string invalidPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (userUid <= 0 || string.IsNullOrWhiteSpace(invalidPath))
        {
            return;
        }
        var loading = BorrowEntry(userUid);
        try
        {
            await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                Task<string> pending;
                var invalidating = false;
                lock (syncRoot)
                {
                    ObjectDisposedException.ThrowIf(isDisposed, this);
                    var entry = entries[userUid].GetAwaiter().GetResult();
                    var path = GetEntryPath(userUid, entry);
                    if (!string.Equals(path, invalidPath, StringComparison.Ordinal))
                    {
                        return;
                    }
                    if (string.Equals(entry.LastInvalidatedUrl, entry.AvatarUrl, StringComparison.Ordinal) &&
                        IsWithin(timeProvider.GetUtcNow(), entry.LastInvalidatedAt, RETRY_DELAY))
                    {
                        return;
                    }
                    if (!refreshes.TryGetValue(userUid, out pending!))
                    {
                        pending = Task.Run(() => InvalidateAndRemoveAsync(userUid, entry, path));
                        refreshes.Add(userUid, pending);
                        invalidating = true;
                    }
                }
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (invalidating)
                {
                    return;
                }
            }
        }
        finally
        {
            ReleaseEntry(userUid);
        }
    }

    /// <summary>开始取消和排空后台任务；需要等待完成时使用异步释放。</summary>
    public void Dispose()
    {
        _ = BeginDispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>取消并排空后台任务后释放资料源，不释放借用的图片下载器。</summary>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return new(BeginDispose());
    }

    private async Task<UserAvatarCacheEntry> LoadEntryAsync(long userUid)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(MetadataPath(userUid), lifetime.Token).ConfigureAwait(false);
            var entry = JsonSerializer.Deserialize<UserAvatarCacheEntry>(bytes);
            return entry is not null && entry.AvatarUrl is not null &&
                (entry.AvatarUrl.Length == 0 || IsValidUrl(entry.AvatarUrl)) ? entry : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                           OperationCanceledException)
        {
            return new();
        }
        finally
        {
            lock (syncRoot)
            {
                loadingUsers.Remove(userUid);
                TrimEntries();
            }
        }
    }

    private async Task<string> RefreshAndRemoveAsync(long userUid, UserAvatarCacheEntry entry, string previousPath)
    {
        try
        {
            var (path, changed) = await RefreshAsync(userUid, entry, previousPath).ConfigureAwait(false);
            if (changed && !lifetime.IsCancellationRequested)
            {
                PublishChange(userUid);
            }
            return path;
        }
        finally
        {
            lock (syncRoot)
            {
                refreshes.Remove(userUid);
                TrimEntries();
            }
        }
    }

    private async Task<string> InvalidateAndRemoveAsync(long userUid, UserAvatarCacheEntry entry, string path)
    {
        try
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Invalid user avatar could not be removed for {0}: {1}", userUid,
                    exception.GetType().Name);
                await StoreEntryAsync(userUid, entry with
                {
                    LastCheckedAt = timeProvider.GetUtcNow(),
                    LastCheckSucceeded = false,
                    LastInvalidatedUrl = entry.AvatarUrl,
                    LastInvalidatedAt = timeProvider.GetUtcNow()
                }).ConfigureAwait(false);
                return path;
            }
            await StoreEntryAsync(userUid, new()
            {
                LastInvalidatedUrl = entry.AvatarUrl,
                LastInvalidatedAt = timeProvider.GetUtcNow()
            }).ConfigureAwait(false);
            return "";
        }
        finally
        {
            lock (syncRoot)
            {
                refreshes.Remove(userUid);
                TrimEntries();
            }
        }
    }

    private async Task<(string Path, bool Changed)> RefreshAsync(long userUid, UserAvatarCacheEntry entry,
        string previousPath)
    {
        try
        {
            string url;
            await querySlots.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                url = await source.GetAvatarUrlAsync(userUid, lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                querySlots.Release();
            }
            if (IsValidUrl(url))
            {
                var path = await images.DownloadAvatarAsync(CacheKey(userUid), url, lifetime.Token).ConfigureAwait(false);
                if (path.Length > 0)
                {
                    var now = timeProvider.GetUtcNow();
                    var updated = entry with
                    {
                        AvatarUrl = url,
                        LastSuccessAt = now,
                        LastCheckedAt = now,
                        LastCheckSucceeded = true
                    };
                    await StoreEntryAsync(userUid, updated).ConfigureAwait(false);
                    return (path, !string.Equals(previousPath, path, StringComparison.Ordinal));
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return (previousPath, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or
                                           OperationCanceledException or Newtonsoft.Json.JsonException)
        {
            Trace.TraceWarning("User avatar refresh failed for {0}: {1}", userUid, exception.GetType().Name);
        }

        await StoreEntryAsync(userUid, entry with
        {
            LastCheckedAt = timeProvider.GetUtcNow(),
            LastCheckSucceeded = false
        }).ConfigureAwait(false);
        return (previousPath, false);
    }

    private async Task StoreEntryAsync(long userUid, UserAvatarCacheEntry entry)
    {
        lock (syncRoot)
        {
            entries[userUid] = Task.FromResult(entry);
        }
        var path = MetadataPath(userUid);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(metadataDirectory);
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(entry), lifetime.Token)
                .ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Trace.TraceWarning("User avatar metadata could not be saved for {0}: {1}", userUid, exception.GetType().Name);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("User avatar metadata temporary file could not be removed: {0}", exception.GetType().Name);
            }
        }
    }

    private string GetCachedPath(long userUid, UserAvatarCacheEntry entry)
    {
        var path = GetEntryPath(userUid, entry);
        return File.Exists(path) ? path : "";
    }

    private string GetEntryPath(long userUid, UserAvatarCacheEntry entry)
    {
        if (!IsValidUrl(entry.AvatarUrl))
        {
            return "";
        }
        return images.GetAvatarFilePath(CacheKey(userUid), entry.AvatarUrl);
    }

    private string MetadataPath(long userUid)
    {
        return Path.Combine(metadataDirectory, $"{userUid.ToString(CultureInfo.InvariantCulture)}.json");
    }

    private static string CacheKey(long userUid)
    {
        return $"user-{userUid.ToString(CultureInfo.InvariantCulture)}";
    }

    private static bool IsValidUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    }

    private static bool IsWithin(DateTimeOffset now, DateTimeOffset timestamp, TimeSpan duration)
    {
        return timestamp != default && timestamp <= now && now - timestamp < duration;
    }

    private Task<UserAvatarCacheEntry> BorrowEntry(long userUid)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (!entries.TryGetValue(userUid, out var loading))
            {
                loadingUsers.Add(userUid);
                loading = Task.Run(() => LoadEntryAsync(userUid));
                entries.Add(userUid, loading);
            }
            callers[userUid] = callers.GetValueOrDefault(userUid) + 1;
            TouchUser(userUid);
            TrimEntries();
            return loading;
        }
    }

    private void ReleaseEntry(long userUid)
    {
        lock (syncRoot)
        {
            if (callers[userUid] == 1)
            {
                callers.Remove(userUid);
            }
            else
            {
                callers[userUid]--;
            }
            TrimEntries();
        }
    }

    private void TouchUser(long userUid)
    {
        if (recencyNodes.TryGetValue(userUid, out var node))
        {
            recency.Remove(node);
        }
        else
        {
            node = new(userUid);
            recencyNodes.Add(userUid, node);
        }
        recency.AddLast(node);
    }

    private void TrimEntries()
    {
        var node = recency.First;
        while (entries.Count > maxCachedUsers && node is not null)
        {
            var next = node.Next;
            var userUid = node.Value;
            if (!callers.ContainsKey(userUid) && !refreshes.ContainsKey(userUid) && !loadingUsers.Contains(userUid))
            {
                entries.Remove(userUid);
                recencyNodes.Remove(userUid);
                recency.Remove(node);
            }
            node = next;
        }
    }

    private void PublishChange(long userUid)
    {
        var subscribers = AvatarChanged;
        if (subscribers is null)
        {
            return;
        }
        foreach (Action<long> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(userUid);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("User avatar update subscriber failed: {0}", exception.GetType().Name);
            }
        }
    }

    private Task BeginDispose()
    {
        lock (syncRoot)
        {
            if (disposal is not null)
            {
                return disposal;
            }
            isDisposed = true;
            lifetime.Cancel();
            var pending = entries.Values.Cast<Task>().Concat(refreshes.Values).ToArray();
            disposal = DisposeAfterPendingAsync(pending);
            return disposal;
        }
    }

    private async Task DisposeAfterPendingAsync(Task[] pending)
    {
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            if (source is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
            querySlots.Dispose();
            lifetime.Dispose();
        }
    }
}
