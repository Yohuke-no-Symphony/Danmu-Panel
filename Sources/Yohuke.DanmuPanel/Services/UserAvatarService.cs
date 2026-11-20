using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Workspace;
using UserAvatarCacheKey = (long UserUid, long CacheRevision);

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
    private readonly Dictionary<UserAvatarCacheKey, Task<UserAvatarCacheEntry>> entries = [];
    private readonly Dictionary<UserAvatarCacheKey, Task<string>> refreshes = [];
    private readonly Dictionary<UserAvatarCacheKey, int> callers = [];
    private readonly HashSet<UserAvatarCacheKey> loadingUsers = [];
    private readonly LinkedList<UserAvatarCacheKey> recency = [];
    private readonly Dictionary<UserAvatarCacheKey, LinkedListNode<UserAvatarCacheKey>> recencyNodes = [];
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
        var key = (UserUid: userUid, CacheRevision: images.Cache.Revision);
        var loading = BorrowEntry(key);
        try
        {
            await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task<string> refreshing;
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(isDisposed, this);
                if (key.CacheRevision != images.Cache.Revision)
                {
                    return "";
                }
                var entry = entries[key].GetAwaiter().GetResult();
                var path = GetCachedPath(userUid, entry);
                var now = timeProvider.GetUtcNow();
                if (path.Length > 0 && entry.LastCheckSucceeded && entry.LastCheckedAt <= now &&
                    IsWithin(now, entry.LastSuccessAt, FRESH_DURATION) ||
                    !entry.LastCheckSucceeded && IsWithin(now, entry.LastCheckedAt, RETRY_DELAY))
                {
                    return path;
                }
                if (!refreshes.TryGetValue(key, out refreshing!))
                {
                    refreshing = Task.Run(() => RefreshAndRemoveAsync(key, entry, path));
                    refreshes.Add(key, refreshing);
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
            ReleaseEntry(key);
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
        var key = (UserUid: userUid, CacheRevision: images.Cache.Revision);
        var loading = BorrowEntry(key);
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
                    if (key.CacheRevision != images.Cache.Revision)
                    {
                        return;
                    }
                    var entry = entries[key].GetAwaiter().GetResult();
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
                    if (!refreshes.TryGetValue(key, out pending!))
                    {
                        pending = Task.Run(() => InvalidateAndRemoveAsync(key, entry, path));
                        refreshes.Add(key, pending);
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
            ReleaseEntry(key);
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

    private async Task<UserAvatarCacheEntry> LoadEntryAsync(UserAvatarCacheKey key)
    {
        try
        {
            await using var stream = new FileStream(MetadataPath(key.UserUid), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, bufferSize: 4096, useAsync: true);
            var entry = await JsonSerializer.DeserializeAsync<UserAvatarCacheEntry>(stream, cancellationToken: lifetime.Token)
                .ConfigureAwait(false);
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
                loadingUsers.Remove(key);
                TrimEntries();
            }
        }
    }

    private async Task<string> RefreshAndRemoveAsync(UserAvatarCacheKey key, UserAvatarCacheEntry entry, string previousPath)
    {
        try
        {
            var (path, changed) = await RefreshAsync(key, entry, previousPath).ConfigureAwait(false);
            if (key.CacheRevision != images.Cache.Revision)
            {
                return "";
            }
            if (changed && !lifetime.IsCancellationRequested)
            {
                PublishChange(key.UserUid);
            }
            return path;
        }
        finally
        {
            lock (syncRoot)
            {
                refreshes.Remove(key);
                TrimEntries();
            }
        }
    }

    private async Task<string> InvalidateAndRemoveAsync(UserAvatarCacheKey key, UserAvatarCacheEntry entry, string path)
    {
        try
        {
            try
            {
                if (!await images.Cache.TryDeleteAsync(path, key.CacheRevision).ConfigureAwait(false))
                {
                    return "";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Invalid user avatar could not be removed for {0}: {1}", key.UserUid,
                    exception.GetType().Name);
                await StoreEntryAsync(key, entry with
                {
                    LastCheckedAt = timeProvider.GetUtcNow(),
                    LastCheckSucceeded = false,
                    LastInvalidatedUrl = entry.AvatarUrl,
                    LastInvalidatedAt = timeProvider.GetUtcNow()
                }).ConfigureAwait(false);
                return path;
            }
            await StoreEntryAsync(key, new()
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
                refreshes.Remove(key);
                TrimEntries();
            }
        }
    }

    private async Task<(string Path, bool Changed)> RefreshAsync(UserAvatarCacheKey key, UserAvatarCacheEntry entry,
        string previousPath)
    {
        try
        {
            string url;
            await querySlots.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                if (key.CacheRevision != images.Cache.Revision)
                {
                    return ("", false);
                }
                url = await source.GetAvatarUrlAsync(key.UserUid, lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                querySlots.Release();
            }
            if (IsValidUrl(url))
            {
                var path = await images.DownloadAvatarAsync(CacheKey(key.UserUid), url, key.CacheRevision, lifetime.Token)
                    .ConfigureAwait(false);
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
                    await StoreEntryAsync(key, updated).ConfigureAwait(false);
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
            Trace.TraceWarning("User avatar refresh failed for {0}: {1}", key.UserUid, exception.GetType().Name);
        }

        await StoreEntryAsync(key, entry with
        {
            LastCheckedAt = timeProvider.GetUtcNow(),
            LastCheckSucceeded = false
        }).ConfigureAwait(false);
        return (previousPath, false);
    }

    private async Task StoreEntryAsync(UserAvatarCacheKey key, UserAvatarCacheEntry entry)
    {
        lock (syncRoot)
        {
            entries[key] = Task.FromResult(entry);
        }
        try
        {
            await images.Cache.TryWriteAsync(MetadataPath(key.UserUid), JsonSerializer.SerializeToUtf8Bytes(entry),
                key.CacheRevision, lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Trace.TraceWarning("User avatar metadata could not be saved for {0}: {1}", key.UserUid, exception.GetType().Name);
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

    private Task<UserAvatarCacheEntry> BorrowEntry(UserAvatarCacheKey key)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (!entries.TryGetValue(key, out var loading))
            {
                loadingUsers.Add(key);
                loading = Task.Run(() => LoadEntryAsync(key));
                entries.Add(key, loading);
            }
            callers[key] = callers.GetValueOrDefault(key) + 1;
            TouchUser(key);
            TrimEntries();
            return loading;
        }
    }

    private void ReleaseEntry(UserAvatarCacheKey key)
    {
        lock (syncRoot)
        {
            if (callers[key] == 1)
            {
                callers.Remove(key);
            }
            else
            {
                callers[key]--;
            }
            TrimEntries();
        }
    }

    private void TouchUser(UserAvatarCacheKey key)
    {
        if (recencyNodes.TryGetValue(key, out var node))
        {
            recency.Remove(node);
        }
        else
        {
            node = new(key);
            recencyNodes.Add(key, node);
        }
        recency.AddLast(node);
    }

    private void TrimEntries()
    {
        var node = recency.First;
        while (entries.Count > maxCachedUsers && node is not null)
        {
            var next = node.Next;
            var key = node.Value;
            if (!callers.ContainsKey(key) && !refreshes.ContainsKey(key) && !loadingUsers.Contains(key))
            {
                entries.Remove(key);
                recencyNodes.Remove(key);
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
