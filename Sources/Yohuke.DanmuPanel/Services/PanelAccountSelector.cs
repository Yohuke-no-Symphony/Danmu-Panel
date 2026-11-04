using Yohuke.DanmuPanel.Models.State;

namespace Yohuke.DanmuPanel.Services;

/// <summary>按房间、工作区、全局顺序选择监听账号。</summary>
public static class PanelAccountSelector
{
    /// <summary>返回当前房间使用的账号，未配置时允许匿名监听。</summary>
    public static BilibiliAccount? Resolve(PanelConfiguration configuration, WorkspaceState workspace, RoomTabState? room)
    {
        var id = room?.AccountOverrideId ?? workspace.AccountOverrideId ?? configuration.Settings.DefaultAccountId;
        return configuration.Accounts.FirstOrDefault(account => account.Id == id);
    }
}
