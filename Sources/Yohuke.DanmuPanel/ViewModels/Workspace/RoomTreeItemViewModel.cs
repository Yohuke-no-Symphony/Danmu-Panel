using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>直播间树节点的选择和连接状态。</summary>
public sealed partial class RoomTreeItemViewModel : ObservableObject
{
    public WorkspaceState Workspace { get; }
    public RoomTabState State { get; }
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private string connectionStatus = "";
    [ObservableProperty] private string avatarPlaceholderText = "";

    /// <summary>创建直播间与所属工作区的显式关联。</summary>
    public RoomTreeItemViewModel(WorkspaceState workspace, RoomTabState state)
    {
        Workspace = workspace;
        State = state;
    }
}
