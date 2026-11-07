using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Yohuke.DanmuPanel.Views;

/// <summary>承载工作区与房间树，双击保留为视图手势。</summary>
public sealed partial class WorkspaceSidebarSurface : UserControl
{
    /// <summary>加载树形布局。</summary>
    public WorkspaceSidebarSurface()
    {
        InitializeComponent();
    }

    private void Workspace_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        this.FindAncestorOfType<WorkspaceView>()!.Workspace_OnDoubleTapped(sender, e);
    }
}
