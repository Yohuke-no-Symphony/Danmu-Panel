namespace Yohuke.DanmuPanel.Models.Workspace;

/// <summary>分享文件中的直播间公共资料，不包含本地身份及阅读记录。</summary>
public sealed class WorkspaceShareRoom
{
    public string RoomId { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string OwnerUid { get; set; } = "";
}
