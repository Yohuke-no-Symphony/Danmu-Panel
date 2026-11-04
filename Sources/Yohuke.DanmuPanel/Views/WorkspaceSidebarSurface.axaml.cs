using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Yohuke.DanmuPanel.Views;

/// <summary>按 Neo 原始树结构承载工作区和房间上下文菜单。</summary>
public sealed partial class WorkspaceSidebarSurface : UserControl
{
    /// <summary>加载树形 AXAML。</summary>
    public WorkspaceSidebarSurface()
    {
        InitializeComponent();
    }

    private void AddRoom_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).AddRoom_OnClick(sender, e);
    }

    private void AddWorkspace_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).AddWorkspace_OnClick(sender, e);
    }

    private void DeleteRoom_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).DeleteRoom_OnClick(sender, e);
    }

    private void DeleteWorkspace_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).DeleteWorkspace_OnClick(sender, e);
    }

    private void EditWorkspace_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).EditWorkspace_OnClick(sender, e);
    }

    private void Export_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).Export_OnClick(sender, e);
    }

    private void Import_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).Import_OnClick(sender, e);
    }

    private void RoomDown_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).RoomDown_OnClick(sender, e);
    }

    private void RoomUp_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).RoomUp_OnClick(sender, e);
    }

    private void SelectRoom_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).SelectRoom_OnClick(sender, e);
    }

    private void SelectWorkspace_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).SelectWorkspace_OnClick(sender, e);
    }

    private void WorkspaceDown_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).WorkspaceDown_OnClick(sender, e);
    }

    private void WorkspaceUp_OnClick(object? sender, RoutedEventArgs e)
    {
        ((WorkspaceView)DataContext!).WorkspaceUp_OnClick(sender, e);
    }

    private void Workspace_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        ((WorkspaceView)DataContext!).Workspace_OnDoubleTapped(sender, e);
    }

}
