using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed class WorkbenchHeaderActionsTemplate : IDataTemplate
{
    /// <inheritdoc />
    public bool Match(object? data)
    {
        return data is WorkbenchDockPanel;
    }

    /// <inheritdoc />
    public Control? Build(object? data)
    {
        if (data is not WorkbenchDockPanel panel ||
            panel.View is not IWorkbenchHeaderActions { HeaderActionsTemplate: { } template })
        {
            return null;
        }
        var actions = template.Build(panel.View);
        if (actions is not null)
        {
            actions.DataContext = panel.View;
        }
        return actions;
    }
}
