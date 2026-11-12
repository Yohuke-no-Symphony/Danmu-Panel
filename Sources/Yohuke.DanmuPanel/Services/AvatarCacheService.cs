using Yohuke.DanmuPanel.I18n;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Models.Logging;

namespace Yohuke.DanmuPanel.Services;

/// <summary>
/// 提供主播头像拉取与本地缓存能力。
/// </summary>
public sealed class AvatarCacheService : IRoomAvatarService, IDisposable
{
    private const int MAX_IMAGE_BYTES = 8 * 1024 * 1024;
    private static readonly TimeSpan DOWNLOAD_TIMEOUT = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> SUPPORTED_EXTENSIONS = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif"
    };

    private readonly AppDirectoryService directoryService;
    private readonly HttpClient httpClient;
    private readonly bool disposeHttpClient;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> downloads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AvatarDownloadOwnerGate> ownerLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim downloadSlots = new(4);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object syncRoot = new();
    private bool isDisposed;

    internal int ActiveOwnerGateCount
    {
        get
        {
            lock (syncRoot)
            {
                return ownerLocks.Count;
            }
        }
    }

    /// <summary>
    /// 初始化头像缓存服务。
    /// </summary>
    public AvatarCacheService(AppDirectoryService directoryService, HttpClient? httpClient = null)
    {
        this.directoryService = directoryService;
        this.httpClient = httpClient ?? new();
        disposeHttpClient = httpClient is null;
    }

    /// <summary>
    /// 释放内部 HTTP 客户端。
    /// </summary>
    public void Dispose()
    {
        Task<string>[] pending;
        lock (syncRoot)
        {
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;
            lifetime.Cancel();
            pending = downloads.Values.Where(value => value.IsValueCreated).Select(value => value.Value).ToArray();
        }
        _ = DisposeAfterDownloadsAsync(pending);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 获取指定直播间主播头像缓存路径。
    /// </summary>
    public async Task<string> GetRoomAvatarAsync(
        string roomId,
        string? cookie = "",
        CancellationToken cancellationToken = default)
    {
        var result = await GetRoomAvatarResultAsync(roomId, "", cookie, cancellationToken);
        return result.AvatarPath;
    }

    /// <summary>
    /// 获取指定直播间主播信息和头像缓存路径。
    /// </summary>
    public async Task<RoomAvatarResult> GetRoomAvatarResultAsync(
        string roomId,
        string ownerUid = "",
        string? cookie = "",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(isDisposed, this);
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = requestLifetime.Token;
        try
        {
            using var api = new BilibiliApi(cookie ?? "", httpClient: httpClient);

            if (long.TryParse(ownerUid.Trim(), out var parsedOwnerUid))
            {
                var userResult =
                    await GetUserAvatarResultAsync(api, parsedOwnerUid, ownerUid.Trim(), cancellationToken);

                if (!string.IsNullOrWhiteSpace(userResult.AvatarPath))
                {
                    return userResult;
                }
            }

            if (!long.TryParse(roomId, out var parsedRoomId))
            {
                return new();
            }

            var response = await api.GetRoomInfoAsync(parsedRoomId, cancellationToken: cancellationToken);
            var resolvedOwnerUid = response.Data?.RoomInfo?.Uid > 0
                ? response.Data.RoomInfo.Uid.ToString()
                : ownerUid.Trim();
            var cacheKey = string.IsNullOrWhiteSpace(resolvedOwnerUid) ? roomId : resolvedOwnerUid;
            var avatarUrl = response.Data?.AnchorInfo?.BaseInfo?.Face;
            var avatarPath = await DownloadAvatarAsync(cacheKey, avatarUrl, cancellationToken);

            return new()
            {
                OwnerUid = resolvedOwnerUid,
                OwnerName = response.Data?.AnchorInfo?.BaseInfo?.Name ?? "",
                AvatarPath = avatarPath
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StartupLog.Append(Localization.Format("Services.Avatar.FetchFailed", roomId), AppLogLevel.ERROR, Localization.Get("Services.Avatar.Source"), exception);
            return new();
        }
    }

    private async Task<RoomAvatarResult> GetUserAvatarResultAsync(
        BilibiliApi api,
        long parsedOwnerUid,
        string ownerUid,
        CancellationToken cancellationToken)
    {
        try
        {
            var cardResponse =
                await api.GetUserCardContainerAsync(parsedOwnerUid, cancellationToken: cancellationToken);
            var card = cardResponse.Data?.Card;
            var avatarPath = await DownloadAvatarAsync(ownerUid, card?.Face, cancellationToken);

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                return new()
                {
                    OwnerUid = ownerUid,
                    OwnerName = card?.Name ?? "",
                    AvatarPath = avatarPath
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StartupLog.Append(Localization.Format("Services.Avatar.PrimaryFailed", ownerUid), AppLogLevel.WARNING, Localization.Get("Services.Avatar.Source"), exception);
        }

        try
        {
            var userResponse = await api.GetUserCardAsync(parsedOwnerUid, cancellationToken: cancellationToken);
            var avatarPath = await DownloadAvatarAsync(ownerUid, userResponse.Data?.Face, cancellationToken);

            if (!string.IsNullOrWhiteSpace(avatarPath))
            {
                return new()
                {
                    OwnerUid = ownerUid,
                    OwnerName = userResponse.Data?.Name ?? "",
                    AvatarPath = avatarPath
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StartupLog.Append(Localization.Format("Services.Avatar.FallbackFailed", ownerUid), AppLogLevel.WARNING, Localization.Get("Services.Avatar.Source"), exception);
        }

        return new();
    }

    /// <summary>
    /// 下载头像并返回缓存文件路径。
    /// </summary>
    public async Task<string> DownloadAvatarAsync(
        string roomId,
        string? avatarUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId) ||
            string.IsNullOrWhiteSpace(avatarUrl) ||
            !Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return "";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var filePath = GetAvatarFilePath(roomId, uri.AbsoluteUri);
        Task<string> task;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            task = downloads.GetOrAdd(filePath, _ => new(() => DownloadAndRemoveAsync(roomId, uri, filePath),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadAndRemoveAsync(string roomId, Uri uri, string filePath)
    {
        var ownerKey = NormalizeFileNamePart(roomId);
        var ownerGate = BorrowOwnerGate(ownerKey);
        try
        {
            await ownerGate.Semaphore.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                await downloadSlots.WaitAsync(lifetime.Token).ConfigureAwait(false);
                try
                {
                    return await DownloadAsync(roomId, uri, filePath, lifetime.Token).ConfigureAwait(false);
                }
                finally
                {
                    downloadSlots.Release();
                }
            }
            finally
            {
                ownerGate.Semaphore.Release();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return "";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                           UnauthorizedAccessException or OperationCanceledException)
        {
            StartupLog.Append(Localization.Format("Services.Avatar.DownloadFailed", roomId), AppLogLevel.WARNING, Localization.Get("Services.Avatar.Source"), exception);
            return "";
        }
        finally
        {
            ReturnOwnerGate(ownerKey, ownerGate);
            downloads.TryRemove(filePath, out _);
        }
    }

    private AvatarDownloadOwnerGate BorrowOwnerGate(string ownerKey)
    {
        lock (syncRoot)
        {
            if (!ownerLocks.TryGetValue(ownerKey, out var gate))
            {
                gate = new();
                ownerLocks.Add(ownerKey, gate);
            }
            gate.BorrowerCount++;
            return gate;
        }
    }

    private void ReturnOwnerGate(string ownerKey, AvatarDownloadOwnerGate gate)
    {
        lock (syncRoot)
        {
            gate.BorrowerCount--;
            if (gate.BorrowerCount == 0)
            {
                ownerLocks.Remove(ownerKey);
                gate.Semaphore.Dispose();
            }
        }
    }

    private async Task<string> DownloadAsync(string roomId, Uri uri, string filePath, CancellationToken cancellationToken)
    {
        if (File.Exists(filePath) && IsSupportedImage(await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false)))
        {
            return filePath;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DOWNLOAD_TIMEOUT);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", BaseApi.DEFAULT_USER_AGENT);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MAX_IMAGE_BYTES)
        {
            return "";
        }
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var image = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (image.Length + count > MAX_IMAGE_BYTES)
            {
                return "";
            }
            image.Write(buffer, 0, count);
        }
        var bytes = image.ToArray();
        if (!IsSupportedImage(bytes))
        {
            return "";
        }
        Directory.CreateDirectory(directoryService.AvatarCacheDirectory);
        var temporary = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, filePath, true);
            DeleteOldRoomAvatarFiles(roomId, filePath);
            return filePath;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool IsSupportedImage(ReadOnlySpan<byte> bytes)
    {
        return bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
               bytes.StartsWith(new byte[] { 255, 216, 255 }) || bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ||
               (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8));
    }

    private async Task DisposeAfterDownloadsAsync(Task<string>[] pending)
    {
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            if (disposeHttpClient)
            {
                httpClient.Dispose();
            }
            downloadSlots.Dispose();
            lifetime.Dispose();
        }
    }

    /// <summary>
    /// 获取头像缓存文件路径。
    /// </summary>
    public string GetAvatarFilePath(string roomId, string avatarUrl)
    {
        return Path.Combine(directoryService.AvatarCacheDirectory, CreateCacheFileName(roomId, avatarUrl));
    }

    /// <summary>
    /// 创建稳定的头像缓存文件名。
    /// </summary>
    public static string CreateCacheFileName(string roomId, string avatarUrl)
    {
        var normalizedRoomId = NormalizeFileNamePart(roomId);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(avatarUrl)))[..12]
            .ToLowerInvariant();
        var extension = GetSafeExtension(avatarUrl);
        return $"{normalizedRoomId}-{hash}{extension}";
    }

    private void DeleteOldRoomAvatarFiles(string roomId, string currentFilePath)
    {
        var prefix = $"{NormalizeFileNamePart(roomId)}-";

        foreach (var filePath in Directory.EnumerateFiles(directoryService.AvatarCacheDirectory, $"{prefix}*"))
        {
            if (string.Equals(filePath, currentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(filePath);
            }
            catch (Exception exception)
            {
                StartupLog.Append(Localization.Get("Services.Avatar.CleanupFailed"), AppLogLevel.WARNING, Localization.Get("Services.Avatar.Source"), exception);
            }
        }
    }

    private static string NormalizeFileNamePart(string value)
    {
        var chars = value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .ToArray();
        return chars.Length == 0 ? "room" : new(chars);
    }

    private static string GetSafeExtension(string avatarUrl)
    {
        if (!Uri.TryCreate(avatarUrl, UriKind.Absolute, out var uri))
        {
            return ".jpg";
        }

        var extension = Path.GetExtension(uri.LocalPath);
        return SUPPORTED_EXTENSIONS.Contains(extension) ? extension.ToLowerInvariant() : ".jpg";
    }
}
