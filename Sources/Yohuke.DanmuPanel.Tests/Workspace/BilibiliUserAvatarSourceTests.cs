using System.Net;
using Yohuke.DanmuPanel.Services;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证公开用户头像查询的主接口、回退和取消行为。</summary>
public sealed class BilibiliUserAvatarSourceTests
{
    /// <summary>成功的公开名片查询直接返回 HTTPS 头像，不调用回退接口。</summary>
    [Fact]
    public async Task CardLookupReturnsNormalizedAvatarWithoutFallback()
    {
        var calls = 0;
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            calls++;
            Assert.Equal("/x/web-interface/card", request.RequestUri!.AbsolutePath);
            Assert.Contains("mid=123", request.RequestUri.Query);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(JsonResponse("""{"code":0,"data":{"card":{"face":"//example.com/avatar.png"}}}"""));
        }));
        using var source = new BilibiliUserAvatarSource(http);

        Assert.Equal("https://example.com/avatar.png", await source.GetAvatarUrlAsync(123));
        Assert.Equal(1, calls);
    }

    /// <summary>主接口出现 HTTP 或业务错误时，通过现有 WBI 用户资料接口回退。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedCardLookupFallsBackToSignedUserInfo(bool httpFailure)
    {
        var paths = new List<string>();
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path switch
            {
                "/x/web-interface/card" => httpFailure
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : JsonResponse("""{"code":-400,"data":{"card":{"face":"https://example.com/ignored.png"}}}"""),
                "/x/web-interface/nav" => JsonResponse("""{"code":0,"data":{"wbi_img":{"img_url":"https://example.com/0123456789abcdef0123456789abcdef.png","sub_url":"https://example.com/fedcba9876543210fedcba9876543210.png"}}}"""),
                "/x/space/wbi/acc/info" => JsonResponse("""{"code":0,"data":{"face":"https://example.com/fallback.png"}}"""),
                _ => throw new InvalidOperationException(path)
            });
        }));
        using var source = new BilibiliUserAvatarSource(http);

        Assert.Equal("https://example.com/fallback.png", await source.GetAvatarUrlAsync(123));
        Assert.Equal(["/x/web-interface/card", "/x/web-interface/nav", "/x/space/wbi/acc/info"], paths);
    }

    /// <summary>用户取消主接口后立即结束，不继续发送回退请求。</summary>
    [Fact]
    public async Task CancellationDoesNotTriggerFallback()
    {
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AvatarHttpHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("{}");
        }));
        using var source = new BilibiliUserAvatarSource(http);
        using var cancellation = new CancellationTokenSource();
        var request = source.GetAvatarUrlAsync(123, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        return new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
