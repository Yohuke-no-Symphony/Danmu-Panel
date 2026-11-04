using Xunit;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证监听账号的三级覆盖和匿名模式。</summary>
public sealed class PanelAccountSelectorTests
{
    /// <summary>房间优先工作区，工作区优先全局。</summary>
    [Fact]
    public void ResolvesRoomThenWorkspaceThenGlobal()
    {
        var global = new BilibiliAccount { Id = "global" };
        var workspaceAccount = new BilibiliAccount { Id = "workspace" };
        var roomAccount = new BilibiliAccount { Id = "room" };
        var configuration = new PanelConfiguration
        {
            Settings = new() { DefaultAccountId = global.Id },
            Accounts = [global, workspaceAccount, roomAccount]
        };
        var workspace = new WorkspaceState { AccountOverrideId = workspaceAccount.Id };
        var room = new RoomTabState { AccountOverrideId = roomAccount.Id };

        Assert.Same(roomAccount, PanelAccountSelector.Resolve(configuration, workspace, room));
        room.AccountOverrideId = null;
        Assert.Same(workspaceAccount, PanelAccountSelector.Resolve(configuration, workspace, room));
        workspace.AccountOverrideId = null;
        Assert.Same(global, PanelAccountSelector.Resolve(configuration, workspace, room));
    }

    /// <summary>没有账号时允许匿名，未知覆盖不会误用其他账号。</summary>
    [Fact]
    public void MissingAccountDoesNotSelectAnUnrelatedIdentity()
    {
        Assert.Null(PanelAccountSelector.Resolve(new(), new(), null));
        var configuration = new PanelConfiguration
        {
            Settings = new() { DefaultAccountId = "global" },
            Accounts = [new() { Id = "global" }]
        };
        Assert.Null(PanelAccountSelector.Resolve(configuration, new(), new() { AccountOverrideId = "removed" }));
    }
}
