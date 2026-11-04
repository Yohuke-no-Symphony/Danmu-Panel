using Avalonia.Controls;
using Avalonia.Interactivity;
using Yohuke.DanmuPanel.ViewModels.Workspace;

namespace Yohuke.DanmuPanel.Views;

/// <summary>复用 Neo 卡片与头部布局的工作区设置表面。</summary>
public sealed partial class WorkspaceEditorSurface : UserControl
{
    /// <summary>加载 AXAML。</summary>
    public WorkspaceEditorSurface()
    {
        InitializeComponent();
    }

    private async void DeleteRoom_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceEditorWindow window && (sender as Control)?.DataContext is WorkspaceRoomEditorViewModel room)
        {
            await window.DeleteRoomAsync(room);
        }
    }
}
