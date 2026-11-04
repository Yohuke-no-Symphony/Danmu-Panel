using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Yohuke.DanmuPanel.Models.State;

/// <summary>包含多个直播间的工作区。</summary>
public sealed partial class WorkspaceState : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string name = "";
    [ObservableProperty] private bool isExpanded = true;
    public string? AccountOverrideId { get; set; }
    public string? SelectedRoomId { get; set; }
    public ObservableCollection<RoomTabState> Rooms { get; set; } = [];
}
