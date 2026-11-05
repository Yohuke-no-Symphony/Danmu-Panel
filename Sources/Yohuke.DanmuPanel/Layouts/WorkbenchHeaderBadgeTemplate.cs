using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed class WorkbenchHeaderBadgeTemplate : IDataTemplate
{
    /// <inheritdoc />
    public bool Match(object? data)
    {
        return data is WorkbenchDockPanel;
    }

    /// <summary>为标题栏或标签创建独立徽标，并绑定到同一面板实例。</summary>
    public Control? Build(object? data)
    {
        if (data is not WorkbenchDockPanel panel ||
            panel.View is not IWorkbenchHeaderBadge { HeaderBadgeTemplate: { } template })
        {
            return null;
        }

        var badge = template.Build(panel.View);
        if (badge is not null)
        {
            badge.DataContext = panel.View;
        }
        return badge;
    }
}
