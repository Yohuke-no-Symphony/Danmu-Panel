using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>工作区设置中的直播间资料草稿。</summary>
public sealed partial class WorkspaceRoomEditorViewModel : ObservableObject
{
    public RoomTabState State { get; }
    public IReadOnlyList<AccountChoice> AccountChoices { get; }
    [ObservableProperty] private string name;
    [ObservableProperty] private string ownerUid;
    [ObservableProperty] private AccountChoice? selectedAccount;

    /// <summary>复制可编辑字段，房号和阅读标签身份保持不变。</summary>
    public WorkspaceRoomEditorViewModel(RoomTabState state, IReadOnlyList<AccountChoice> accountChoices)
    {
        State = state;
        name = state.Name;
        ownerUid = state.OwnerUid;
        AccountChoices = accountChoices;
        selectedAccount = accountChoices.FirstOrDefault(value => value.Id == state.AccountOverrideId) ?? accountChoices[0];
    }
}
