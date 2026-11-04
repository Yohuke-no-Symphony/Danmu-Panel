using Dock.Controls.DeferredContentControl;
using Dock.Model.Mvvm.Controls;

namespace Yohuke.DanmuPanel.Layouts;

internal sealed class WorkbenchRootDock : RootDock, IDeferredContentPresentation
{
    public bool DeferContentPresentation => false;
}
