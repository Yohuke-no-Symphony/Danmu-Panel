using Avalonia.Controls.Templates;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>为停靠和浮动面板提供位于标题右侧的内容标签。</summary>
public interface IWorkbenchHeaderTabs
{
    /// <summary>以当前面板为数据上下文创建独立的标签控件。</summary>
    IDataTemplate? HeaderTabsTemplate { get; }
}
