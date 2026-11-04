using Avalonia.Controls.Templates;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>为停靠和浮动面板的标题提供可独立创建的状态徽标。</summary>
public interface IWorkbenchHeaderBadge
{
    /// <summary>以当前面板为数据上下文创建标题旁的徽标。</summary>
    IDataTemplate? HeaderBadgeTemplate { get; }
}
