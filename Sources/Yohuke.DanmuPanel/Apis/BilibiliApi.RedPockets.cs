using Newtonsoft.Json.Linq;
using Yohuke.DanmuPanel.Apis.Models.Common;

namespace Yohuke.DanmuPanel.Apis;

public partial class BilibiliApi
{
    /// <summary>读取直播间内当前可查询的红包列表。</summary>
    public Task<ApiResponse<JObject>> GetRedPocketActiveListAsync(
        long roomId,
        int number = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var headers = CreateAccountHeaders(number);
        headers["Referer"] = $"https://live.bilibili.com/{roomId}";
        return GetJsonAsync<ApiResponse<JObject>>(
            "https://api.live.bilibili.com/xlive/lottery-interface/v1/popularityRedPocket/RedPocketActiveList",
            new Dictionary<string, object?>
            {
                ["room_id"] = roomId
            },
            headers,
            timeout,
            cancellationToken);
    }

    /// <summary>读取直播间红包详情，可指定红包ID。</summary>
    public Task<ApiResponse<JObject>> GetRedPocketInfoAsync(
        long roomId,
        long? lotId = null,
        int number = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var headers = CreateAccountHeaders(number);
        headers["Referer"] = $"https://live.bilibili.com/{roomId}";
        return GetJsonAsync<ApiResponse<JObject>>(
            "https://api.live.bilibili.com/xlive/lottery-interface/v1/popularityRedPocket/RedPocketGetInfo",
            new Dictionary<string, object?>
            {
                ["room_id"] = roomId,
                ["lot_id"] = lotId
            },
            headers,
            timeout,
            cancellationToken);
    }

    /// <summary>读取指定红包的开奖用户和奖品信息。</summary>
    public Task<ApiResponse<JObject>> GetRedPocketWinnersAsync(
        long lotId,
        int number = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<ApiResponse<JObject>>(
            "https://api.live.bilibili.com/xlive/lottery-interface/v1/popularityRedPocket/RedPocketGetWinners",
            new Dictionary<string, object?>
            {
                ["lot_id"] = lotId,
                ["lotId"] = lotId
            },
            CreateAccountHeaders(number),
            timeout,
            cancellationToken);
    }
}
