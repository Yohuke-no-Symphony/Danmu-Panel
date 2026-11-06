using System.Diagnostics;
using Yohuke.DanmuPanel.Apis;

namespace Yohuke.DanmuPanel.Services;

/// <summary>使用 B 站公开名片查询头像，必要时回退到签名用户资料接口。</summary>
public sealed class BilibiliUserAvatarSource : IUserAvatarSource, IDisposable
{
    private readonly BilibiliApi api;
    private bool isDisposed;

    /// <summary>初始化用户资料查询；注入的 HTTP 客户端由调用方持有。</summary>
    public BilibiliUserAvatarSource(HttpClient? httpClient = null)
    {
        api = new(httpClient: httpClient);
    }

    /// <inheritdoc />
    public async Task<string> GetAvatarUrlAsync(long userUid, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (userUid <= 0)
        {
            return "";
        }

        try
        {
            var response = await api.GetUserCardContainerAsync(userUid, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var url = response.Code == 0 ? NormalizeUrl(response.Data?.Card?.Face) : "";
            if (url.Length > 0)
            {
                return url;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsLookupFailure(exception))
        {
            Trace.TraceWarning("User avatar card lookup failed for {0}: {1}", userUid, exception.GetType().Name);
        }

        try
        {
            var response = await api.GetUserCardAsync(userUid, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.Code == 0 ? NormalizeUrl(response.Data?.Face) : "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsLookupFailure(exception))
        {
            Trace.TraceWarning("User avatar fallback lookup failed for {0}: {1}", userUid, exception.GetType().Name);
            return "";
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        api.Dispose();
        GC.SuppressFinalize(this);
    }

    private static bool IsLookupFailure(Exception exception)
    {
        return exception is HttpRequestException or OperationCanceledException or Newtonsoft.Json.JsonException or
            InvalidOperationException;
    }

    private static string NormalizeUrl(string? value)
    {
        var url = value?.Trim() ?? "";
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = $"https:{url}";
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = $"https://{url[7..]}";
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : "";
    }
}
