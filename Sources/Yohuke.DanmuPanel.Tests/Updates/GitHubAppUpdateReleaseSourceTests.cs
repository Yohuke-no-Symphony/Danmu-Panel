using System.Net;
using System.Text.Json;
using Yohuke.DanmuPanel.Services.Updates;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Updates;

/// <summary>验证正式发布请求、外部数据校验、超时与取消的网络边界。</summary>
public sealed class GitHubAppUpdateReleaseSourceTests
{
    private const string RELEASE_PAGE_PREFIX = "https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/";

    /// <summary>读取正式发布元数据，接受大小写 v 前缀及二至四段数字版本。</summary>
    [Theory]
    [InlineData("v0.0.10", "0.0.10.0")]
    [InlineData("V2.0", "2.0.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    public async Task PublishedReleasePreservesItsMetadata(string tagName, string expectedVersion)
    {
        using var handler = JsonHandler(ReleaseJson(tagName));
        using var client = new HttpClient(handler);
        var source = new GitHubAppUpdateReleaseSource(client);

        var release = await source.GetLatestAsync();

        Assert.NotNull(release);
        Assert.Equal(Version.Parse(expectedVersion), release.Version);
        Assert.Equal(tagName, release.TagName);
        Assert.Equal("发布名称", release.Name);
        Assert.Equal("修复与改进\n第二行", release.ReleaseNotes);
        Assert.Equal(RELEASE_PAGE_PREFIX + tagName, release.ReleaseUri.AbsoluteUri);
    }

    /// <summary>发布名称和说明可选，缺少时使用标签名及空说明。</summary>
    [Fact]
    public async Task OptionalReleaseTextHasUsableDefaults()
    {
        using var handler = JsonHandler(ReleaseJson("v1.2.3", name: null, body: null));
        using var client = new HttpClient(handler);

        var release = await new GitHubAppUpdateReleaseSource(client).GetLatestAsync();

        Assert.NotNull(release);
        Assert.Equal("v1.2.3", release.Name);
        Assert.Equal(string.Empty, release.ReleaseNotes);
    }

    /// <summary>公开请求限定本项目，并携带 GitHub 必要请求头。</summary>
    [Fact]
    public async Task RequestUsesPublicRepositoryWithoutAccountCredentials()
    {
        using var handler = JsonHandler(ReleaseJson("v1.0.0"));
        using var client = new HttpClient(handler);

        await new GitHubAppUpdateReleaseSource(client).GetLatestAsync();

        Assert.Equal("https://api.github.com/repos/Yohuke-no-Symphony/DanmuPanel/releases/latest",
            handler.RequestUri!.AbsoluteUri);
        Assert.Equal("application/vnd.github+json", handler.Accept);
        Assert.Contains("Yohuke-DanmuPanel", handler.UserAgent);
        Assert.Equal("2026-03-10", handler.ApiVersion);
        Assert.False(handler.HasCredentials);
    }

    /// <summary>仓库尚无正式发布时返回空发布，供上层区分无发布和已是最新。</summary>
    [Fact]
    public async Task MissingReleaseReturnsNoRelease()
    {
        using var handler = JsonHandler(string.Empty, HttpStatusCode.NotFound);
        using var client = new HttpClient(handler);

        Assert.Null(await new GitHubAppUpdateReleaseSource(client).GetLatestAsync());
    }

    /// <summary>草稿和预发布不会被当作正式版本。</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NonFinalReleaseIsExcluded(bool draft, bool prerelease)
    {
        using var handler = JsonHandler(ReleaseJson("v1.0.0", draft: draft, prerelease: prerelease));
        using var client = new HttpClient(handler);

        Assert.Null(await new GitHubAppUpdateReleaseSource(client).GetLatestAsync());
    }

    /// <summary>空响应、损坏 JSON 和缺少必要字段均属于检查失败。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ broken")]
    [InlineData("{\"tag_name\":\"v1.0.0\",\"html_url\":\"https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0\"}")]
    public async Task InvalidPayloadRemainsFailure(string json)
    {
        using var handler = JsonHandler(json);
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubAppUpdateReleaseSource(client).GetLatestAsync());
    }

    /// <summary>不支持的标签不能误报已是最新或生成下载提示。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2.3+build")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("v1.+2.3")]
    [InlineData("v1. 2.3")]
    [InlineData("v1.2147483648.0")]
    public async Task UnsupportedTagRemainsFailure(string tagName)
    {
        using var handler = JsonHandler(ReleaseJson(tagName));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubAppUpdateReleaseSource(client).GetLatestAsync());
    }

    /// <summary>只接受对应标签的本仓库 HTTPS 发布页。</summary>
    [Theory]
    [InlineData("file:///tmp/update")]
    [InlineData("http://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0")]
    [InlineData("https://github.com/other/repo/releases/tag/v1.0.0")]
    [InlineData("https://github.com.evil.example/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0")]
    [InlineData("https://user@github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0")]
    [InlineData("https://github.com:444/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0")]
    [InlineData("https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v2.0.0")]
    [InlineData("https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0?other=1")]
    [InlineData("https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v1.0.0#other")]
    public async Task InvalidReleaseAddressRemainsFailure(string address)
    {
        using var handler = JsonHandler(ReleaseJson("v1.0.0", address));
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubAppUpdateReleaseSource(client).GetLatestAsync());
    }

    /// <summary>权限、限流和服务器错误保留 HTTP 状态以便界面提供准确反馈。</summary>
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsPreserveTheirStatus(HttpStatusCode status)
    {
        using var handler = JsonHandler(string.Empty, status);
        using var client = new HttpClient(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => new GitHubAppUpdateReleaseSource(client).GetLatestAsync());

        Assert.Equal(status, error.StatusCode);
    }

    /// <summary>超时覆盖尚未收到响应头的请求，并与主动取消区分。</summary>
    [Fact]
    public async Task RequestTimeoutIsReportedAsTimeout()
    {
        using var handler = new AppUpdateHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        var source = new GitHubAppUpdateReleaseSource(client, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => source.GetLatestAsync());
    }

    /// <summary>收到响应头后读取正文仍受同一次超时限制。</summary>
    [Fact]
    public async Task TimeoutAlsoCoversResponseBody()
    {
        using var handler = new AppUpdateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellationReadStream())
        }));
        using var client = new HttpClient(handler);
        var source = new GitHubAppUpdateReleaseSource(client, TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<TimeoutException>(() => source.GetLatestAsync());
    }

    /// <summary>调用者取消会停止传输，并保留取消语义。</summary>
    [Fact]
    public async Task CallerCancellationStopsTheRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AppUpdateHttpHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var check = new GitHubAppUpdateReleaseSource(client).GetLatestAsync(cancellation.Token);
        await started.Task;

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    /// <summary>发布源不释放由调用者注入的 HTTP 客户端。</summary>
    [Fact]
    public async Task InjectedClientRemainsUsableAfterTheCheck()
    {
        using var handler = JsonHandler(ReleaseJson("v1.0.0"));
        using var client = new HttpClient(handler);
        await new GitHubAppUpdateReleaseSource(client).GetLatestAsync();

        using var response = await client.GetAsync("https://example.test/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static AppUpdateHttpHandler JsonHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) }));
    }

    private static string ReleaseJson(string tagName, string? address = null, bool draft = false,
        bool prerelease = false, string? name = "发布名称", string? body = "修复与改进\n第二行")
    {
        return JsonSerializer.Serialize(new
        {
            tag_name = tagName,
            html_url = address ?? RELEASE_PAGE_PREFIX + Uri.EscapeDataString(tagName),
            draft,
            prerelease,
            name,
            body
        });
    }
}
