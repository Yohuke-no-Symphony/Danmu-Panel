using System.Net;
using System.Text.Json;
using Yohuke.DanmuPanel.Apis.Models.GitHub;
using Yohuke.DanmuPanel.Models.Updates;

namespace Yohuke.DanmuPanel.Services.Updates;

/// <summary>通过 GitHub 公开 API 读取 DanmuPanel 最新正式发布。</summary>
public sealed class GitHubAppUpdateReleaseSource : IAppUpdateReleaseSource
{
    private const string LATEST_RELEASE_URL = "https://api.github.com/repos/Yohuke-no-Symphony/DanmuPanel/releases/latest";
    private const string RELEASE_PAGE_PATH = "/Yohuke-no-Symphony/DanmuPanel/releases/tag/";
    private readonly HttpClient httpClient;
    private readonly TimeSpan timeout;

    /// <summary>使用调用者管理的 HTTP 客户端；超时包含接收响应头和读取正文。</summary>
    public GitHubAppUpdateReleaseSource(HttpClient httpClient, TimeSpan? timeout = null)
    {
        this.httpClient = httpClient;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <inheritdoc/>
    public async Task<AppUpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(timeout);
        try
        {
            var release = await ReadReleaseAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return release;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The GitHub release check timed out.", exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub returned invalid release data.", exception);
        }
    }

    private async Task<AppUpdateInfo?> ReadReleaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LATEST_RELEASE_URL);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("Yohuke-DanmuPanel");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
        if (release.IsDraft || release.IsPrerelease)
        {
            return null;
        }

        var tagName = release.TagName?.Trim() ?? string.Empty;
        var version = AppUpdateVersion.ParseTag(tagName);
        if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != RELEASE_PAGE_PATH + Uri.EscapeDataString(tagName))
        {
            throw new InvalidDataException("GitHub returned an invalid release page address.");
        }

        return new(version, tagName, string.IsNullOrWhiteSpace(release.Name) ? tagName : release.Name,
            release.Body ?? string.Empty, uri);
    }
}
