using Avalonia.Controls.Templates;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>为 Dock 标题栏提供可随停靠位置重新创建的面板操作。</summary>
public interface IWorkbenchHeaderActions
{
    /// <summary>以当前面板为数据上下文创建标题栏操作。</summary>
    IDataTemplate? HeaderActionsTemplate { get; }
}
