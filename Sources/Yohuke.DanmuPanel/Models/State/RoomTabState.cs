using CommunityToolkit.Mvvm.ComponentModel;

namespace Yohuke.DanmuPanel.Models.State;

/// <summary>独立保存阅读状态的直播间标签。</summary>
public sealed partial class RoomTabState : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RoomId { get; set; } = "";
    [ObservableProperty] private string name = "";
    public string OwnerUid { get; set; } = "";
    public string AvatarPath { get; set; } = "";
    public string? AccountOverrideId { get; set; }
}
