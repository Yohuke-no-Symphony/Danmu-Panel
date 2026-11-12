using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuCore.Apis.Models.Common;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services;

/// <summary>将全站昵称搜索和公开名片转换为特别关注用户，不依赖用户开通直播间。</summary>
public sealed partial class BilibiliUserSearchService : IBilibiliUserSearchService
{
    private readonly HttpClient? httpClient;

    /// <summary>创建用户搜索服务；调用方保留注入客户端的所有权。</summary>
    public BilibiliUserSearchService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task<UserSearchPage> SearchAsync(
        string keyword,
        int page = 1,
        string? cookie = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyword);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        cancellationToken.ThrowIfCancellationRequested();
        keyword = keyword.Trim();
        if (keyword.Length == 0)
        {
            return new() { Page = page };
        }
        using var api = new BilibiliApi(cookie ?? "", timeout, httpClient);
        try
        {
            UserSearchPage result;
            if (keyword.All(char.IsAsciiDigit))
            {
                if (!long.TryParse(keyword, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid <= 0)
                {
                    return IssuePage(page, "Services.BilibiliUserSearch.InvalidIdentifier");
                }
                if (page > 1)
                {
                    return new() { Page = page, TotalPages = 1 };
                }
                result = await SearchUidAsync(api, uid, cookie, timeout, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await SearchNicknameAsync(api, keyword, page, cookie, timeout, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return IssuePage(page, "Services.BilibiliUserSearch.SearchFailed");
        }
    }

    private static async Task<UserSearchPage> SearchNicknameAsync(
        BilibiliApi api, string keyword, int page, string? cookie, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var response = await api.SearchUsersAsync(keyword, page: page, cookie: cookie ?? "",
            timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.Code != 0)
        {
            return ApiIssuePage(page, response);
        }
        if (response.Data?.Result is not { } users)
        {
            return IssuePage(page, "Services.BilibiliUserSearch.ResultsMissing");
        }
        return new()
        {
            Page = page,
            TotalPages = response.Data.TotalPages,
            HasMore = response.Data.TotalPages is { } totalPages && page < totalPages,
            Results = users.Where(user => user is { Uid: > 0 }).DistinctBy(user => user.Uid)
                .Select(user => new UserSearchResult
                {
                    UserUid = user.Uid,
                    DisplayName = CleanText(user.Name),
                    AvatarUrl = NormalizeAvatarUrl(user.AvatarUrl)
                }).ToList()
        };
    }

    private static async Task<UserSearchPage> SearchUidAsync(
        BilibiliApi api, long uid, string? cookie, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var response = await api.GetUserCardContainerAsync(uid, timeout, cancellationToken, cookie).ConfigureAwait(false);
        if (response.Code != 0)
        {
            return ApiIssuePage(1, response);
        }
        if (response.Data?.Card is not { } user || user.Uid != uid)
        {
            return IssuePage(1, "Services.BilibiliUserSearch.ResultsMissing");
        }
        return new()
        {
            Page = 1,
            TotalPages = 1,
            Results = [new()
            {
                UserUid = user.Uid,
                DisplayName = CleanText(user.Name),
                AvatarUrl = NormalizeAvatarUrl(user.Face)
            }]
        };
    }

    private static UserSearchPage ApiIssuePage<T>(int page, ApiResponse<T> response)
    {
        var message = response.Message ?? response.Msg;
        return new()
        {
            Page = page,
            Issues = [new()
            {
                ApiCode = response.Code,
                Message = message ?? "",
                MessageKey = string.IsNullOrWhiteSpace(message) ? "Services.BilibiliUserSearch.SearchFailed" : null
            }]
        };
    }

    private static UserSearchPage IssuePage(int page, string messageKey)
    {
        return new() { Page = page, Issues = [new() { MessageKey = messageKey }] };
    }

    private static string CleanText(string? text)
    {
        return WebUtility.HtmlDecode(HtmlTagPattern().Replace(text ?? "", "")).Trim();
    }

    private static string NormalizeAvatarUrl(string? value)
    {
        var url = value?.Trim() ?? "";
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url[7..];
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : "";
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagPattern();
}
