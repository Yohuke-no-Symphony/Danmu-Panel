using System.Net;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证头像缓存复用、替换和房间身份查找。</summary>
public sealed class AvatarCacheServiceTests
{
    private static readonly byte[] PNG = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGMI6LnzH4QZYAwAXRQK3dFXl4QAAAAASUVORK5CYII=");

    /// <summary>同一地址命中磁盘缓存，头像更新仅保留同一主播的新文件。</summary>
    [AvaloniaFact]
    public async Task DownloadReusesCacheAndRemovesPreviousOwnerImage()
    {
        using var directory = new MessageStoreTestDirectory();
        var calls = 0;
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            calls++;
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(ImageResponse());
        }));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var first = await cache.DownloadAvatarAsync("123", "https://example.com/first.png");
        Assert.Equal(first, await cache.DownloadAvatarAsync("123", "https://example.com/first.png"));
        Assert.Equal(1, calls);
        Assert.Equal(PNG, await File.ReadAllBytesAsync(first));
        var other = await cache.DownloadAvatarAsync("456", "https://example.com/other.png");
        var replacement = await cache.DownloadAvatarAsync("123", "https://example.com/new.webp");
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(replacement));
        Assert.True(File.Exists(other));
        using var reopened = new AvatarCacheService(new(directory.Path), http);
        Assert.Equal(replacement, await reopened.DownloadAvatarAsync("123", "https://example.com/new.webp"));
        Assert.Equal(3, calls);
    }

    /// <summary>并发请求共享完成后的文件，不产生重复下载或临时缓存残留。</summary>
    [AvaloniaFact]
    public async Task ConcurrentDownloadsReuseOneCompletedFile()
    {
        using var directory = new MessageStoreTestDirectory();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AvatarHttpHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            return ImageResponse();
        }));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var first = cache.DownloadAvatarAsync("123", "https://example.com/avatar.png");
        var second = cache.DownloadAvatarAsync("123", "https://example.com/avatar.png");
        release.SetResult();
        Assert.Equal(await first, await second);
        Assert.Equal(1, calls);
        Assert.Single(Directory.GetFiles(new AppDirectoryService(directory.Path).AvatarCacheDirectory));
    }

    /// <summary>大量不同用户完成下载后，不保留已经空闲的用户下载门。</summary>
    [AvaloniaFact]
    public async Task CompletedDistinctOwnersDoNotRetainDownloadGates()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = new HttpClient(new AvatarHttpHandler((_, _) => Task.FromResult(ImageResponse())));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var downloads = Enumerable.Range(1, 128)
            .Select(owner => cache.DownloadAvatarAsync($"user-{owner}", $"https://example.com/{owner}.png"));

        var paths = await Task.WhenAll(downloads).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(paths, path => Assert.True(File.Exists(path)));
        Assert.Equal(0, cache.ActiveOwnerGateCount);
        Assert.Equal(paths[0], await cache.DownloadAvatarAsync("user-1", "https://example.com/1.png"));
        Assert.Equal(0, cache.ActiveOwnerGateCount);
    }

    /// <summary>同一用户的不同头像严格串行，取消共享任务的一个等待者不释放仍在使用的下载门。</summary>
    [AvaloniaFact]
    public async Task QueuedOwnerDownloadsRemainSerializedUntilTheirLastBorrowerExits()
    {
        using var directory = new MessageStoreTestDirectory();
        var started = Enumerable.Range(0, 3).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var release = Enumerable.Range(0, 3).Select(_ =>
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var active = 0;
        var overlap = 0;
        var calls = 0;
        using var http = new HttpClient(new AvatarHttpHandler(async (request, token) =>
        {
            var index = int.Parse(Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath));
            Interlocked.Increment(ref calls);
            if (Interlocked.Increment(ref active) != 1)
            {
                Interlocked.Exchange(ref overlap, 1);
            }
            started[index].TrySetResult();
            try
            {
                await release[index].Task.WaitAsync(token);
                return ImageResponse();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        try
        {
            var first = cache.DownloadAvatarAsync("user-123", "https://example.com/0.png");
            await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var cancelled = cache.DownloadAvatarAsync("user-123", "https://example.com/1.png", cancellation.Token);
            var second = cache.DownloadAvatarAsync("user-123", "https://example.com/1.png");
            var third = cache.DownloadAvatarAsync("user-123", "https://example.com/2.png");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal(1, cache.ActiveOwnerGateCount);
            Assert.False(started[1].Task.IsCompleted);
            Assert.False(started[2].Task.IsCompleted);

            release[0].SetResult();
            var firstPath = await first.WaitAsync(TimeSpan.FromSeconds(5));
            await started[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(firstPath));
            Assert.Equal(1, cache.ActiveOwnerGateCount);
            Assert.False(started[2].Task.IsCompleted);

            release[1].SetResult();
            var secondPath = await second.WaitAsync(TimeSpan.FromSeconds(5));
            await started[2].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            Assert.Equal(1, cache.ActiveOwnerGateCount);

            release[2].SetResult();
            var finalPath = await third.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(File.Exists(secondPath));
            Assert.True(File.Exists(finalPath));
            Assert.Equal(0, cache.ActiveOwnerGateCount);
            Assert.Equal(0, overlap);
            Assert.Equal(3, calls);
            Assert.Single(Directory.GetFiles(new AppDirectoryService(directory.Path).AvatarCacheDirectory));
        }
        finally
        {
            foreach (var completion in release)
            {
                completion.TrySetResult();
            }
        }
    }

    /// <summary>释放缓存服务时，持有和排队等待同一用户下载门的任务均可结束并归还门。</summary>
    [AvaloniaFact]
    public async Task DisposalCancelsQueuedOwnerDownloadsAndReleasesTheirGate()
    {
        using var directory = new MessageStoreTestDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var http = new HttpClient(new AvatarHttpHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ImageResponse();
        }));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var first = cache.DownloadAvatarAsync("user-123", "https://example.com/0.png");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = cache.DownloadAvatarAsync("user-123", "https://example.com/1.png");
        var third = cache.DownloadAvatarAsync("user-123", "https://example.com/2.png");
        Assert.Equal(1, cache.ActiveOwnerGateCount);

        cache.Dispose();
        var paths = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(paths, path => Assert.Equal("", path));
        Assert.Equal(0, cache.ActiveOwnerGateCount);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            cache.DownloadAvatarAsync("user-123", "https://example.com/3.png"));
    }

    /// <summary>异常图片不能覆盖旧头像缓存。</summary>
    [AvaloniaTheory]
    [InlineData(200, "not an image")]
    [InlineData(503, "unavailable")]
    public async Task FailedReplacementPreservesCachedAvatar(int status, string body)
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = new HttpClient(new AvatarHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/first.png" ? ImageResponse() : new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body)
            })));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var existing = await cache.DownloadAvatarAsync("123", "https://example.com/first.png");
        Assert.Equal("", await cache.DownloadAvatarAsync("123", "https://example.com/bad.png"));
        Assert.True(File.Exists(existing));
        Assert.Single(Directory.GetFiles(new AppDirectoryService(directory.Path).AvatarCacheDirectory));
    }

    /// <summary>有 UID 时优先公开名片接口，且不会向图片站发送 Cookie。</summary>
    [AvaloniaFact]
    public async Task OwnerLookupUsesCardAndSeparatesAvatarRequestHeaders()
    {
        using var directory = new MessageStoreTestDirectory();
        var requests = new List<string>();
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.Host == "example.com")
            {
                Assert.False(request.Headers.Contains("Cookie"));
                return Task.FromResult(ImageResponse());
            }
            Assert.Equal("/x/web-interface/card", request.RequestUri.AbsolutePath);
            return Task.FromResult(JsonResponse("""{"code":0,"data":{"card":{"mid":456,"name":"主播","face":"https://example.com/avatar.png"}}}"""));
        }));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var result = await cache.GetRoomAvatarResultAsync("123", "456", "SESSDATA=test");
        Assert.Equal("456", result.OwnerUid);
        Assert.Equal("主播", result.OwnerName);
        Assert.True(File.Exists(result.AvatarPath));
        Assert.Equal(2, requests.Count);
    }

    /// <summary>没有 UID 时通过房间资料补齐主播身份并缓存头像。</summary>
    [AvaloniaFact]
    public async Task RoomLookupResolvesMissingOwnerIdentity()
    {
        using var directory = new MessageStoreTestDirectory();
        using var http = new HttpClient(new AvatarHttpHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "example.com" ? ImageResponse() : JsonResponse(
                """{"code":0,"data":{"room_info":{"uid":456},"anchor_info":{"base_info":{"uname":"主播","face":"https://example.com/avatar.png"}}}}"""))));
        using var cache = new AvatarCacheService(new(directory.Path), http);
        var result = await cache.GetRoomAvatarResultAsync("123");
        Assert.Equal("456", result.OwnerUid);
        Assert.True(File.Exists(result.AvatarPath));
        Assert.StartsWith("456-", Path.GetFileName(result.AvatarPath));
    }

    private static HttpResponseMessage ImageResponse()
    {
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(PNG) };
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        return new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
