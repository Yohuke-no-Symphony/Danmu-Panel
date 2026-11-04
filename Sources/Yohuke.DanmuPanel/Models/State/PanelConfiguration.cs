using System.Collections.ObjectModel;

namespace Yohuke.DanmuPanel.Models.State;

/// <summary>独立于消息数据库的应用配置。</summary>
public sealed class PanelConfiguration
{
    public PanelSettings Settings { get; set; } = new();
    public ObservableCollection<BilibiliAccount> Accounts { get; set; } = [];
    public ObservableCollection<WorkspaceState> Workspaces { get; set; } = [];
}
