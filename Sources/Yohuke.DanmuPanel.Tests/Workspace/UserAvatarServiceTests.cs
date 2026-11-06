using System.Net;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证用户头像共享、持久缓存、后台更新与生命周期。</summary>
public sealed class UserAvatarServiceTests
{
    private static readonly byte[] PNG = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGMI6LnzH4QZYAwAXRQK3dFXl4QAAAAASUVORK5CYII=");

    /// <summary>无效 UID 不产生用户资料或图片请求。</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidUserReturnsEmptyWithoutLookup(long userUid)
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var source = new DelegateUserAvatarSource((_, _) => throw new InvalidOperationException());
        await using var service = new UserAvatarService(new(directory.Path), images, source);

        Assert.Equal("", await service.GetAvatarAsync(userUid));
        Assert.Equal(0, source.Calls);
    }

    /// <summary>同 UID 共享资料和图片请求，取消一个等待者不取消其他等待者。</summary>
    [AvaloniaFact]
    public async Task ConcurrentWaitersShareLookupAndCallerCancellationIsIndependent()
    {
        using var directory = new MessageStoreTestDirectory();
        var imageCalls = 0;
        using var http = ImageClient(() => Interlocked.Increment(ref imageCalls));
        using var images = new AvatarCacheService(new(directory.Path), http);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateUserAvatarSource(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return "https://example.com/avatar.png";
        });
        await using var service = new UserAvatarService(new(directory.Path), images, source);
        using var cancelled = new CancellationTokenSource();
        var first = service.GetAvatarAsync(123, cancelled.Token);
        var second = service.GetAvatarAsync(123);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();

        var path = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(path));
        Assert.Equal(path, await service.GetAvatarAsync(123));
        Assert.Equal(1, source.Calls);
        Assert.Equal(1, imageCalls);
    }

    /// <summary>重启后直接复用未过期的用户元数据与磁盘图片。</summary>
    [AvaloniaFact]
    public async Task FreshAvatarSurvivesServiceRestartWithoutNetwork()
    {
        using var directory = new MessageStoreTestDirectory();
        var imageCalls = 0;
        using var http = ImageClient(() => imageCalls++);
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/avatar.png"));
        string path;
        await using (var service = new UserAvatarService(new(directory.Path), images, source, clock))
        {
            path = await service.GetAvatarAsync(123);
        }
        var reopenedSource = new DelegateUserAvatarSource((_, _) => throw new InvalidOperationException());
        await using var reopened = new UserAvatarService(new(directory.Path), images, reopenedSource, clock);

        Assert.Equal(path, await reopened.GetAvatarAsync(123));
        Assert.Equal(0, reopenedSource.Calls);
        Assert.Equal(1, imageCalls);
        Assert.True(File.Exists(MetadataPath(directory.Path, 123)));
    }

    /// <summary>已过期的持久头像立即返回，后台成功更新后通知并清理旧版本。</summary>
    [AvaloniaFact]
    public async Task StaleAvatarReturnsImmediatelyAndRefreshesAllConsumers()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        var firstSource = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/old.png"));
        string oldPath;
        await using (var initial = new UserAvatarService(new(directory.Path), images, firstSource, clock))
        {
            oldPath = await initial.GetAvatarAsync(123);
        }
        clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateUserAvatarSource(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return "https://example.com/new.png";
        });
        await using var service = new UserAvatarService(new(directory.Path), images, source, clock);
        service.AvatarChanged += userUid => updated.TrySetResult(userUid);

        Assert.Equal(oldPath, await service.GetAvatarAsync(123).WaitAsync(TimeSpan.FromSeconds(1)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(oldPath));
        release.SetResult();
        Assert.Equal(123, await updated.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var newPath = await service.GetAvatarAsync(123);

        Assert.NotEqual(oldPath, newPath);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        Assert.Equal(1, source.Calls);
    }

    /// <summary>更新失败保留旧图，冷却在重启后仍生效，冷却结束后可恢复。</summary>
    [AvaloniaFact]
    public async Task FailedUpdatePreservesOldAvatarAndPersistsRetryBackoff()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = new HttpClient(new AvatarHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/bad.png"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PNG) })));
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        var calls = 0;
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? "https://example.com/old.png" : "https://example.com/bad.png"));
        string oldPath;
        await using (var service = new UserAvatarService(new(directory.Path), images, source, clock))
        {
            oldPath = await service.GetAvatarAsync(123);
            clock.Advance(TimeSpan.FromHours(7));
            Assert.Equal(oldPath, await service.GetAvatarAsync(123));
            await WaitForMetadataAsync(directory.Path, 123,
                entry => entry.LastCheckedAt == clock.GetUtcNow() && !entry.LastCheckSucceeded);
            Assert.Equal(oldPath, await service.GetAvatarAsync(123));
            Assert.True(File.Exists(oldPath));
            Assert.Equal(2, source.Calls);
        }
        var replacement = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/recovered.png"));
        await using var reopened = new UserAvatarService(new(directory.Path), images, replacement, clock);
        Assert.Equal(oldPath, await reopened.GetAvatarAsync(123));
        Assert.Equal(0, replacement.Calls);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reopened.AvatarChanged += _ => changed.TrySetResult();
        Assert.Equal(oldPath, await reopened.GetAvatarAsync(123));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(oldPath, await reopened.GetAvatarAsync(123));
        Assert.Equal(1, replacement.Calls);
    }

    /// <summary>首次查询失败也设置五分钟冷却，之后允许重新获取。</summary>
    [AvaloniaFact]
    public async Task MissingAvatarRetriesOnlyAfterBackoffExpires()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        var calls = 0;
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? "" : "https://example.com/avatar.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source, clock);

        Assert.Equal("", await service.GetAvatarAsync(123));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal("", await service.GetAvatarAsync(123));
        Assert.Equal(1, source.Calls);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.True(File.Exists(await service.GetAvatarAsync(123)));
        Assert.Equal(2, source.Calls);
    }

    /// <summary>损坏的元数据和丢失的图片会触发恢复，不造成永久空头像。</summary>
    [AvaloniaFact]
    public async Task CorruptMetadataAndMissingImageRecoverThroughSource()
    {
        using var directory = new MessageStoreTestDirectory();
        var metadata = MetadataPath(directory.Path, 123);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, "{broken");
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/avatar.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source);
        var path = await service.GetAvatarAsync(123);
        Assert.True(File.Exists(path));
        File.Delete(path);

        Assert.Equal(path, await service.GetAvatarAsync(123));
        Assert.True(File.Exists(path));
        Assert.Equal(2, source.Calls);
    }

    /// <summary>大量不同用户同时出现时，最多并发查询四份用户资料。</summary>
    [AvaloniaFact]
    public async Task ProfileLookupsHaveGlobalConcurrencyLimit()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var concurrencySync = new object();
        var source = new DelegateUserAvatarSource(async (userUid, token) =>
        {
            var current = Interlocked.Increment(ref active);
            lock (concurrencySync)
            {
                maximum = Math.Max(maximum, current);
            }
            if (current == 4)
            {
                fourStarted.TrySetResult();
            }
            try
            {
                await release.Task.WaitAsync(token);
                return $"https://example.com/{userUid}.png";
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        await using var service = new UserAvatarService(new(directory.Path), images, source);
        var requests = Enumerable.Range(1, 12).Select(uid => service.GetAvatarAsync(uid)).ToArray();
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, source.Calls);
        release.SetResult();

        var paths = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Equal(4, maximum);
        Assert.Equal(12, source.Calls);
    }

    /// <summary>释放时取消并排空资料查询，再释放源，借用的下载服务仍可使用。</summary>
    [AvaloniaFact]
    public async Task DisposalDrainsQueriesBeforeDisposingSourceAndBorrowsDownloader()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateUserAvatarSource(async (_, token) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await finish.Task;
                throw;
            }
            return "";
        });
        await using var service = new UserAvatarService(new(directory.Path), images, source);
        var request = service.GetAvatarAsync(123);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disposing = service.DisposeAsync().AsTask();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(source.IsDisposed);
        Assert.False(disposing.IsCompleted);
        finish.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("", await request);
        Assert.True(source.IsDisposed);
        Assert.True(File.Exists(await images.DownloadAvatarAsync("other", "https://example.com/other.png")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetAvatarAsync(123));
    }

    /// <summary>只保留有限用户元数据，被淘汰的用户仍从磁盘恢复而不查询网络。</summary>
    [AvaloniaFact]
    public async Task IdleMetadataHasBoundedMemoryAndEvictedUsersReloadFromDisk()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var source = new DelegateUserAvatarSource((uid, _) => Task.FromResult($"https://example.com/{uid}.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source, null, 2);
        var first = await service.GetAvatarAsync(1);
        foreach (var uid in Enumerable.Range(2, 5))
        {
            Assert.True(File.Exists(await service.GetAvatarAsync(uid)));
        }

        Assert.Equal(2, service.CachedUserCount);
        Assert.Equal(first, await service.GetAvatarAsync(1));
        Assert.Equal(2, service.CachedUserCount);
        Assert.Equal(6, source.Calls);
    }

    /// <summary>容量裁剪不能移除活跃查询，也不能使同用户产生重复资料请求。</summary>
    [AvaloniaFact]
    public async Task EvictionKeepsActiveUserQueriesShared()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateUserAvatarSource(async (uid, token) =>
        {
            if (uid == 123)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return $"https://example.com/{uid}.png";
        });
        await using var service = new UserAvatarService(new(directory.Path), images, source, null, 1);
        var first = service.GetAvatarAsync(123);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(await service.GetAvatarAsync(456)));
        Assert.True(File.Exists(await service.GetAvatarAsync(789)));
        var second = service.GetAvatarAsync(123);
        release.SetResult();

        Assert.Equal(await first, await second);
        Assert.Equal(3, source.Calls);
        Assert.Equal(1, service.CachedUserCount);
    }

    /// <summary>未来时间戳不会让成功或失败元数据永久跳过重新验证。</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FutureMetadataTimestampsDoNotSuppressRefresh(bool lastCheckSucceeded)
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        const string oldUrl = "https://example.com/old.png";
        var oldPath = await images.DownloadAvatarAsync("user-123", oldUrl);
        var metadata = MetadataPath(directory.Path, 123);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new UserAvatarCacheEntry
        {
            AvatarUrl = oldUrl,
            LastSuccessAt = clock.GetUtcNow().AddYears(100),
            LastCheckedAt = clock.GetUtcNow().AddYears(100),
            LastCheckSucceeded = lastCheckSucceeded
        }));
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/new.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source, clock);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AvatarChanged += _ => changed.TrySetResult();

        Assert.Equal(oldPath, await service.GetAvatarAsync(123));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(oldPath, await service.GetAvatarAsync(123));
        Assert.Equal(1, source.Calls);
    }

    /// <summary>不可反序列化或含空 URL 的元数据不会留下永久失败的内存任务。</summary>
    [AvaloniaTheory]
    [InlineData("{\"AvatarUrl\":null}")]
    [InlineData("{\"AvatarUrl\":42}")]
    [InlineData("{\"LastCheckedAt\":{}}")]
    public async Task InvalidMetadataTypesRecoverThroughSource(string content)
    {
        using var directory = new MessageStoreTestDirectory();
        var metadata = MetadataPath(directory.Path, 123);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        await File.WriteAllTextAsync(metadata, content);
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/avatar.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source);

        var path = await service.GetAvatarAsync(123);
        Assert.True(File.Exists(path));
        Assert.Equal(path, await service.GetAvatarAsync(123));
        Assert.Equal(1, source.Calls);
    }

    /// <summary>收到解码失败反馈后删除当前文件，相同 URL 能重新下载有效内容。</summary>
    [AvaloniaFact]
    public async Task InvalidatingCurrentAvatarAllowsSameUrlToBeDownloadedAgain()
    {
        using var directory = new MessageStoreTestDirectory();
        var calls = 0;
        using var http = ImageClient(() => calls++);
        using var images = new AvatarCacheService(new(directory.Path), http);
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/avatar.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source);
        var path = await service.GetAvatarAsync(123);

        await service.InvalidateAvatarAsync(123, path);
        Assert.False(File.Exists(path));
        Assert.Equal(path, await service.GetAvatarAsync(123));
        Assert.True(File.Exists(path));
        Assert.Equal(2, calls);
        Assert.Equal(2, source.Calls);
    }

    /// <summary>多行旧图的迟到反馈不会重复删除同一 URL 重新下载后的有效图。</summary>
    [AvaloniaFact]
    public async Task RepeatedInvalidationIsCoalescedWithoutBlockingFirstRetry()
    {
        using var directory = new MessageStoreTestDirectory();
        var calls = 0;
        using var http = ImageClient(() => calls++);
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/avatar.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source, clock);
        var path = await service.GetAvatarAsync(123);
        var notifications = 0;
        service.AvatarChanged += _ => notifications++;

        await service.InvalidateAvatarAsync(123, path);
        Assert.Equal(0, notifications);
        Assert.Equal(path, await service.GetAvatarAsync(123));
        Assert.Equal(1, notifications);
        await service.InvalidateAvatarAsync(123, path);
        Assert.True(File.Exists(path));
        Assert.Equal(path, await service.GetAvatarAsync(123));
        Assert.Equal(2, calls);
        Assert.Equal(2, source.Calls);
        clock.Advance(TimeSpan.FromMinutes(5));
        await service.InvalidateAvatarAsync(123, path);
        Assert.False(File.Exists(path));
    }

    /// <summary>失效合并标记可跨服务重启，旧控件反馈不会删掉重新下载后的同 URL 文件。</summary>
    [AvaloniaFact]
    public async Task RepeatedInvalidationBackoffSurvivesServiceRestart()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        string path;
        await using (var service = new UserAvatarService(new(directory.Path), images,
            new DelegateUserAvatarSource((_, _) => Task.FromResult("https://example.com/avatar.png")), clock))
        {
            path = await service.GetAvatarAsync(123);
            await service.InvalidateAvatarAsync(123, path);
            Assert.Equal(path, await service.GetAvatarAsync(123));
        }
        var source = new DelegateUserAvatarSource((_, _) => throw new InvalidOperationException());
        await using var reopened = new UserAvatarService(new(directory.Path), images, source, clock);

        await reopened.InvalidateAvatarAsync(123, path);
        Assert.True(File.Exists(path));
        Assert.Equal(path, await reopened.GetAvatarAsync(123));
        Assert.Equal(0, source.Calls);
    }

    /// <summary>读取旧路径失败的延迟反馈不会删除已经更新的头像。</summary>
    [AvaloniaFact]
    public async Task DelayedInvalidationCannotDeleteNewerAvatar()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        var calls = 0;
        var source = new DelegateUserAvatarSource((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? "https://example.com/old.png" : "https://example.com/new.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source, clock);
        var oldPath = await service.GetAvatarAsync(123);
        clock.Advance(TimeSpan.FromHours(7));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.AvatarChanged += _ => changed.TrySetResult();
        Assert.Equal(oldPath, await service.GetAvatarAsync(123));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newPath = await service.GetAvatarAsync(123);

        await service.InvalidateAvatarAsync(123, oldPath);
        Assert.Equal(newPath, await service.GetAvatarAsync(123));
        Assert.True(File.Exists(newPath));
        Assert.Equal(2, source.Calls);
    }

    /// <summary>失效接口仅能影响指定用户的当前文件，也可以恢复已丢失的文件。</summary>
    [AvaloniaFact]
    public async Task InvalidatingDifferentUserPathDoesNothingAndMissingFilesRecover()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var source = new DelegateUserAvatarSource((uid, _) => Task.FromResult($"https://example.com/{uid}.png"));
        await using var service = new UserAvatarService(new(directory.Path), images, source);
        var first = await service.GetAvatarAsync(123);
        var second = await service.GetAvatarAsync(456);

        await service.InvalidateAvatarAsync(123, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        File.Delete(first);
        await service.InvalidateAvatarAsync(123, first);
        Assert.True(File.Exists(await service.GetAvatarAsync(123)));
        Assert.Equal(3, source.Calls);
    }

    /// <summary>没有头像的失败冷却也能跨服务重启恢复。</summary>
    [AvaloniaFact]
    public async Task MissingAvatarBackoffSurvivesRestart()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = ImageClient();
        using var images = new AvatarCacheService(new(directory.Path), http);
        var clock = new UserAvatarTestTimeProvider();
        await using (var initial = new UserAvatarService(new(directory.Path), images,
            new DelegateUserAvatarSource((_, _) => Task.FromResult("")), clock))
        {
            Assert.Equal("", await initial.GetAvatarAsync(123));
        }
        var source = new DelegateUserAvatarSource((_, _) => throw new InvalidOperationException());
        await using var reopened = new UserAvatarService(new(directory.Path), images, source, clock);

        Assert.Equal("", await reopened.GetAvatarAsync(123));
        Assert.Equal(0, source.Calls);
    }

    private static HttpClient ImageClient(Action? requested = null)
    {
        return new(new AvatarHttpHandler((_, _) =>
        {
            requested?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PNG) });
        }));
    }

    private static string MetadataPath(string directory, long uid)
    {
        return Path.Combine(new AppDirectoryService(directory).AvatarCacheDirectory, "Users", $"{uid}.json");
    }

    private static async Task WaitForMetadataAsync(string directory, long uid, Func<UserAvatarCacheEntry, bool> matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var entry = JsonSerializer.Deserialize<UserAvatarCacheEntry>(
                await File.ReadAllTextAsync(MetadataPath(directory, uid), timeout.Token));
            if (entry is not null && matches(entry))
            {
                return;
            }
            await Task.Delay(10, timeout.Token);
        }
    }
}
