using Newtonsoft.Json.Linq;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.Apis.Models.Bilibili;
using Yohuke.DanmuPanel.Apis.Models.Common;
using Yohuke.DanmuPanel.Apis.Protocols.Bilibili;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Services;

/// <summary>通过账号登录态补查红包列表、内容与开奖结果。</summary>
public sealed class BilibiliRedPocketService : IBilibiliRedPocketService
{
    private readonly HttpClient? httpClient;

    /// <summary>初始化红包查询服务，注入的HTTP客户端由调用方管理。</summary>
    public BilibiliRedPocketService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BilibiliRedPocketMessage>> GetActiveAsync(
        string roomId,
        string cookie,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var parsedRoomId = ValidateRoomId(roomId);
        using var api = new BilibiliApi(cookie, timeout, httpClient);
        var response = await api.GetRedPocketActiveListAsync(parsedRoomId, timeout: timeout, cancellationToken: cancellationToken);
        EnsureSuccess(response, cookie);
        var list = response.Data?["list"] as JArray ?? response.Data?["lot_info"] as JArray;
        return ParseInfoList(roomId, list);
    }

    /// <inheritdoc/>
    public async Task<BilibiliRedPocketMessage?> GetInfoAsync(
        string roomId,
        long lotId,
        string cookie,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var parsedRoomId = ValidateRoomId(roomId);
        ArgumentOutOfRangeException.ThrowIfLessThan(lotId, 1);
        using var api = new BilibiliApi(cookie, timeout, httpClient);
        var response = await api.GetRedPocketInfoAsync(parsedRoomId, lotId, timeout: timeout, cancellationToken: cancellationToken);
        EnsureSuccess(response, cookie);
        var list = response.Data?["lot_info"] as JArray;
        return ParseInfoList(roomId, list).FirstOrDefault(message => message.RedPocket.LotId == lotId);
    }

    /// <inheritdoc/>
    public async Task<BilibiliRedPocketMessage?> GetWinnersAsync(
        string roomId,
        long lotId,
        string cookie,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRoomId(roomId);
        ArgumentOutOfRangeException.ThrowIfLessThan(lotId, 1);
        using var api = new BilibiliApi(cookie, timeout, httpClient);
        var response = await api.GetRedPocketWinnersAsync(lotId, timeout: timeout, cancellationToken: cancellationToken);
        EnsureSuccess(response, cookie);
        if (response.Data is null)
        {
            return null;
        }

        var message = BilibiliRedPocketMessageParser.ParseWinners(roomId, lotId, response.Data);
        return message.RedPocket.HasResult ? message : null;
    }

    private static long ValidateRoomId(string roomId)
    {
        if (!long.TryParse(roomId, out var value) || value <= 0)
        {
            throw new ArgumentException(Localization.Get("Room.InvalidRoomId"), nameof(roomId));
        }

        return value;
    }

    private static IReadOnlyList<BilibiliRedPocketMessage> ParseInfoList(string roomId, JArray? values)
    {
        if (values is null)
        {
            return [];
        }

        var messages = new List<BilibiliRedPocketMessage>(values.Count);
        foreach (var value in values)
        {
            if (value is not JObject info)
            {
                throw new InvalidDataException("Red pocket list entry is invalid.");
            }

            messages.Add(BilibiliRedPocketMessageParser.ParseInfo(roomId, info));
        }

        return messages;
    }

    private static void EnsureSuccess(ApiResponse<JObject> response, string cookie)
    {
        if (response.Code == 0)
        {
            return;
        }

        var detail = AppLogSanitizer.Sanitize(response.Message ?? response.Msg ?? "", ApiCookieHelper.Parse(cookie).Values);
        var error = $"{Localization.Format("Room.RedPocket.FailedCode", response.Code)} {detail}".Trim();
        throw response.Code == -101
            ? new BilibiliAuthenticationException(error)
            : new InvalidOperationException(error);
    }
}
