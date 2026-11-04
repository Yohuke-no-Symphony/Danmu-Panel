using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Services;

/// <summary>
/// 获取直播间的主播身份与头像缓存。
/// </summary>
public interface IRoomAvatarService
{
    /// <summary>
    /// 获取指定直播间的主播信息和头像路径。
    /// </summary>
    Task<RoomAvatarResult> GetRoomAvatarResultAsync(
        string roomId,
        string ownerUid = "",
        string? cookie = "",
        CancellationToken cancellationToken = default);
}
