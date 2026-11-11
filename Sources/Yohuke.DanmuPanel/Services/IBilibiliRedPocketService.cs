using Yohuke.DanmuPanel.Apis.Models.Bilibili;

namespace Yohuke.DanmuPanel.Services;

/// <summary>查询直播间红包内容及已经生成的开奖结果。</summary>
public interface IBilibiliRedPocketService
{
    /// <summary>读取当前直播间内可查询的红包列表。</summary>
    Task<IReadOnlyList<BilibiliRedPocketMessage>> GetActiveAsync(
        string roomId,
        string cookie,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取指定红包详情，服务器暂未返回时为空。</summary>
    Task<BilibiliRedPocketMessage?> GetInfoAsync(
        string roomId,
        long lotId,
        string cookie,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>读取指定红包的中奖结果，结果尚未就绪时为空。</summary>
    Task<BilibiliRedPocketMessage?> GetWinnersAsync(
        string roomId,
        long lotId,
        string cookie,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
