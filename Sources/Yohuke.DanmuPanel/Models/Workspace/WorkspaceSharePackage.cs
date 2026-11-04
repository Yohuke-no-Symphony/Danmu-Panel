namespace Yohuke.DanmuPanel.Models.Workspace;

/// <summary>与 Danmu Neo v1 公共字段兼容的工作区分享包。</summary>
public sealed class WorkspaceSharePackage
{
    public int Version { get; set; } = 1;
    public string WorkspaceName { get; set; } = "";
    public List<WorkspaceShareRoom> Rooms { get; set; } = [];
}
