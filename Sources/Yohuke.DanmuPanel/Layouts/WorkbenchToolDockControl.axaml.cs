using Dock.Avalonia.Controls;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed partial class WorkbenchToolDockControl : ToolDockControl
{
    protected override Type StyleKeyOverride => typeof(ToolDockControl);

    public WorkbenchToolDockControl()
    {
        InitializeComponent();
    }
}
