using System.Net;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证头像缓存复用、替换和房间身份查找。</summary>
public sealed class AvatarCacheServiceTests
{
    /// <summary>初始化服务日志使用的本地化资源。</summary>
    public AvatarCacheServiceTests()
    {
        if (!I18n.Localization.IsInitialized)
        {
            I18n.Localization.Initialize(Path.Combine(AppContext.BaseDirectory, "i18n"));
        }
    }

    private static readonly byte[] PNG = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAEUlEQVR4nGMI6LnzH4QZYAwAXRQK3dFXl4QAAAAASUVORK5CYII=");

    /// <summary>同一地址命中磁盘缓存，头像更新仅保留同一主播的新文件。</summary>
    [Fact]
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
    [Fact]
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

    /// <summary>异常图片不能覆盖旧头像缓存。</summary>
    [Theory]
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
    [Fact]
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
    [Fact]
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
