using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>工作区树的选择、账号及房间展示状态。</summary>
public sealed partial class WorkspaceTreeItemViewModel : ObservableObject
{
    public WorkspaceState State { get; }
    public ObservableCollection<RoomTreeItemViewModel> Rooms { get; } = [];
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string accountLabel = "";
    [ObservableProperty] private string roomCountText = "";

    /// <summary>创建不改变持久化对象身份的树节点。</summary>
    public WorkspaceTreeItemViewModel(WorkspaceState state)
    {
        State = state;
    }
}
