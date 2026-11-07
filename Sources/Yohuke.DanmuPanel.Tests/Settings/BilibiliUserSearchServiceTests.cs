using System.Net;
using System.Text.Json;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Workspace;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证特别关注使用的全站用户搜索、UID 查询、分页及取消行为。</summary>
public sealed class BilibiliUserSearchServiceTests
{
    /// <summary>昵称查询面向全部用户，传递分页及账号，清理昵称并保留未开播用户。</summary>
    [Fact]
    public async Task NicknameSearchIncludesOrdinaryUsersAndPreservesPagination()
    {
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            Assert.Equal("/x/web-interface/search/type", request.RequestUri!.AbsolutePath);
            Assert.Contains("search_type=bili_user", request.RequestUri.Query);
            Assert.Contains("page=2", request.RequestUri.Query);
            Assert.Contains(Uri.EscapeDataString("纳米"), request.RequestUri.Query);
            Assert.Equal("https://search.bilibili.com", request.Headers.GetValues("Origin").Single());
            Assert.Contains("SESSDATA=test-session", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(JsonResponse("""
                {"code":0,"data":{"numPages":3,"result":[
                  {"mid":123,"uname":" 小<em class=\"keyword\">纳</em>&amp;米 ","upic":"//example.com/ordinary.png"},
                  {"mid":123,"uname":"重复","upic":"https://example.com/duplicate.png"},
                  {"mid":456,"uname":"第二位","upic":"http://example.com/second.png"},
                  {"mid":0,"uname":"无效用户"}
                ]}}
                """));
        }));
        var service = new BilibiliUserSearchService(http);

        var page = await service.SearchAsync(" 纳米 ", 2, "SESSDATA=test-session");

        Assert.Empty(page.Issues);
        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasMore);
        Assert.Equal(new long[] { 123, 456 }, page.Results.Select(result => result.UserUid));
        Assert.Equal("小纳&米", page.Results[0].DisplayName);
        Assert.Equal("https://example.com/ordinary.png", page.Results[0].AvatarUrl);
        Assert.Equal("https://example.com/second.png", page.Results[1].AvatarUrl);
    }

    /// <summary>UID 查询直接读取公开名片，不要求开通直播间并传递当前账号。</summary>
    [Fact]
    public async Task UidLookupUsesPublicCardWithoutRequiringLiveRoom()
    {
        using var http = new HttpClient(new AvatarHttpHandler((request, _) =>
        {
            Assert.Equal("/x/web-interface/card", request.RequestUri!.AbsolutePath);
            Assert.Contains("mid=123", request.RequestUri.Query);
            Assert.Contains("SESSDATA=uid-session", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(JsonResponse("""
                {"code":0,"data":{"card":{"mid":"123","name":"<em>普通</em>&amp;用户","face":"//example.com/card.png"}}}
                """));
        }));

        var page = await new BilibiliUserSearchService(http).SearchAsync(" 000123 ", cookie: "SESSDATA=uid-session");

        var user = Assert.Single(page.Results);
        Assert.Equal(123, user.UserUid);
        Assert.Equal("普通&用户", user.DisplayName);
        Assert.Equal("https://example.com/card.png", user.AvatarUrl);
        Assert.Equal(1, page.Page);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasMore);
        Assert.Empty(page.Issues);
    }

    /// <summary>空关键词不发请求，UID 的后续页也不会重复查询。</summary>
    [Theory]
    [InlineData(" ", 1)]
    [InlineData("123", 2)]
    public async Task EmptyOrExhaustedUidSearchDoesNotSendRequest(string keyword, int pageNumber)
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) => throw new InvalidOperationException("Unexpected request")));

        var page = await new BilibiliUserSearchService(http).SearchAsync(keyword, pageNumber);

        Assert.Empty(page.Results);
        Assert.Empty(page.Issues);
        Assert.Equal(pageNumber, page.Page);
        Assert.False(page.HasMore);
    }

    /// <summary>零 UID 和超出 Int64 的数字被识别为无效身份，不退回昵称搜索。</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("9223372036854775808")]
    public async Task InvalidUidReturnsIssueWithoutSendingRequest(string keyword)
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) => throw new InvalidOperationException("Unexpected request")));

        var page = await new BilibiliUserSearchService(http).SearchAsync(keyword);

        Assert.Empty(page.Results);
        Assert.Equal("Services.BilibiliUserSearch.InvalidIdentifier", Assert.Single(page.Issues).MessageKey);
    }

    /// <summary>远端业务错误保留错误码及消息，便于用户了解登录或服务限制。</summary>
    [Fact]
    public async Task ApiFailureReturnsCodeAndRemoteMessage()
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"code":-101,"message":"请登录"}"""))));

        var page = await new BilibiliUserSearchService(http).SearchAsync("用户", 3);

        var issue = Assert.Single(page.Issues);
        Assert.Equal(-101, issue.ApiCode);
        Assert.Equal("请登录", issue.Message);
        Assert.Null(issue.MessageKey);
        Assert.Equal(3, page.Page);
        Assert.Empty(page.Results);
        Assert.False(page.HasMore);
    }

    /// <summary>缺失结果结构或 UID 返回不匹配时报告数据错误，避免添加错误身份。</summary>
    [Theory]
    [InlineData("用户", "{\"code\":0,\"data\":{}}")]
    [InlineData("123", "{\"code\":0,\"data\":{\"card\":{\"mid\":456}}}")]
    public async Task MissingOrMismatchedIdentityReturnsDataIssue(string keyword, string response)
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) => Task.FromResult(JsonResponse(response))));

        var page = await new BilibiliUserSearchService(http).SearchAsync(keyword);

        Assert.Empty(page.Results);
        Assert.Equal("Services.BilibiliUserSearch.ResultsMissing", Assert.Single(page.Issues).MessageKey);
    }

    /// <summary>空结果属于成功页面，缺失页数时不会凭空启用加载更多。</summary>
    [Fact]
    public async Task EmptyNicknameResultsDoNotBecomeFailureOrInventMorePages()
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"code":0,"data":{"result":[]}}"""))));

        var page = await new BilibiliUserSearchService(http).SearchAsync("无人匹配");

        Assert.Empty(page.Results);
        Assert.Empty(page.Issues);
        Assert.Null(page.TotalPages);
        Assert.False(page.HasMore);
    }

    /// <summary>非网络头像地址不会流入显示结果。</summary>
    [Theory]
    [InlineData("file:///tmp/avatar.png")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("not-an-address")]
    public async Task InvalidAvatarUrlIsDiscarded(string avatarUrl)
    {
        var response = JsonSerializer.Serialize(new
        {
            code = 0,
            data = new { result = new[] { new { mid = 123, uname = "用户", upic = avatarUrl } } }
        });
        using var http = new HttpClient(new AvatarHttpHandler((_, _) =>
            Task.FromResult(JsonResponse(response))));

        var page = await new BilibiliUserSearchService(http).SearchAsync("用户");

        Assert.Equal("", Assert.Single(page.Results).AvatarUrl);
        Assert.Empty(page.Issues);
    }

    /// <summary>HTTP、超时及 JSON 错误转换为可本地化问题，不将凭据传入错误提示。</summary>
    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("json")]
    public async Task RequestFailuresReturnLocalizedIssue(string failure)
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) => failure switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")),
            _ => Task.FromResult(JsonResponse("{"))
        }));

        var page = await new BilibiliUserSearchService(http).SearchAsync("用户", cookie: "SESSDATA=private-value");

        Assert.Empty(page.Results);
        Assert.Equal("Services.BilibiliUserSearch.SearchFailed", Assert.Single(page.Issues).MessageKey);
    }

    /// <summary>用户取消请求后抛出取消异常，不返回一个伪造的空成功页。</summary>
    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AvatarHttpHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse("{}");
        }));
        using var cancellation = new CancellationTokenSource();
        var request = new BilibiliUserSearchService(http).SearchAsync("用户", cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    /// <summary>每次查询结束后保留调用方注入客户端的所有权，允许复用。</summary>
    [Fact]
    public async Task SearchDoesNotDisposeInjectedHttpClient()
    {
        using var http = new HttpClient(new AvatarHttpHandler((_, _) =>
            Task.FromResult(JsonResponse("""{"code":0,"data":{"result":[]}}"""))));
        var service = new BilibiliUserSearchService(http);

        await service.SearchAsync("首次");
        await service.SearchAsync("再次");
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        return new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
