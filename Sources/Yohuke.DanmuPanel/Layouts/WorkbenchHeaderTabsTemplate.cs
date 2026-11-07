using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed class WorkbenchHeaderTabsTemplate : IDataTemplate
{
    /// <inheritdoc />
    public bool Match(object? data)
    {
        return data is WorkbenchDockPanel;
    }

    /// <summary>为每个标题宿主创建独立标签，并绑定到原面板实例。</summary>
    public Control? Build(object? data)
    {
        if (data is not WorkbenchDockPanel panel ||
            panel.View is not IWorkbenchHeaderTabs { HeaderTabsTemplate: { } template })
        {
            return null;
        }

        var tabs = template.Build(panel.View);
        if (tabs is not null)
        {
            tabs.DataContext = panel.View;
        }
        return tabs;
    }
}
