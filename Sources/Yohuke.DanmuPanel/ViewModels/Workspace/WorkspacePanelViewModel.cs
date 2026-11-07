using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>绑定工作区树操作和当前房间监听状态。</summary>
public sealed partial class WorkspacePanelViewModel : ObservableObject
{
    public ObservableCollection<WorkspaceTreeItemViewModel> Workspaces { get; } = [];
    public IAsyncRelayCommand AddWorkspaceCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IAsyncRelayCommand ToggleListeningCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> SelectWorkspaceCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> AddRoomCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> EditWorkspaceCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> DeleteWorkspaceCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> ExportCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> WorkspaceUpCommand { get; }
    public IAsyncRelayCommand<WorkspaceTreeItemViewModel> WorkspaceDownCommand { get; }
    public IAsyncRelayCommand<RoomTreeItemViewModel> SelectRoomCommand { get; }
    public IAsyncRelayCommand<RoomTreeItemViewModel> RoomUpCommand { get; }
    public IAsyncRelayCommand<RoomTreeItemViewModel> RoomDownCommand { get; }
    public IAsyncRelayCommand<RoomTreeItemViewModel> DeleteRoomCommand { get; }
    [ObservableProperty] private string roomTitle = "";
    [ObservableProperty] private string roomNumber = "";
    [ObservableProperty] private string connectionStatus = "";
    [ObservableProperty] private bool isListening;
    [ObservableProperty] private bool hasRoom;
    [ObservableProperty] private string listenText = "";
    [ObservableProperty] private LucideIconKind listenIcon;

    /// <summary>连接需要窗口或文件选择器参与的操作。</summary>
    public WorkspacePanelViewModel(Func<Task> addWorkspace, Func<Task> import, Func<Task> toggleListening,
        Func<WorkspaceTreeItemViewModel, Task> selectWorkspace, Func<WorkspaceTreeItemViewModel, Task> addRoom,
        Func<WorkspaceTreeItemViewModel, Task> editWorkspace, Func<WorkspaceTreeItemViewModel, Task> deleteWorkspace,
        Func<WorkspaceTreeItemViewModel, Task> export, Func<WorkspaceTreeItemViewModel, Task> workspaceUp,
        Func<WorkspaceTreeItemViewModel, Task> workspaceDown, Func<RoomTreeItemViewModel, Task> selectRoom,
        Func<RoomTreeItemViewModel, Task> roomUp, Func<RoomTreeItemViewModel, Task> roomDown,
        Func<RoomTreeItemViewModel, Task> deleteRoom, Action<Exception> failed)
    {
        AddWorkspaceCommand = Connect(addWorkspace, failed);
        ImportCommand = Connect(import, failed);
        ToggleListeningCommand = Connect(toggleListening, failed);
        SelectWorkspaceCommand = Connect(selectWorkspace, failed);
        AddRoomCommand = Connect(addRoom, failed);
        EditWorkspaceCommand = Connect(editWorkspace, failed);
        DeleteWorkspaceCommand = Connect(deleteWorkspace, failed);
        ExportCommand = Connect(export, failed);
        WorkspaceUpCommand = Connect(workspaceUp, failed);
        WorkspaceDownCommand = Connect(workspaceDown, failed);
        SelectRoomCommand = Connect(selectRoom, failed);
        RoomUpCommand = Connect(roomUp, failed);
        RoomDownCommand = Connect(roomDown, failed);
        DeleteRoomCommand = Connect(deleteRoom, failed);
    }

    private static IAsyncRelayCommand Connect(Func<Task> action, Action<Exception> failed)
    {
        return new AsyncRelayCommand(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                failed(exception);
            }
        });
    }

    private static IAsyncRelayCommand<T> Connect<T>(Func<T, Task> action, Action<Exception> failed) where T : class
    {
        return new AsyncRelayCommand<T>(async item =>
        {
            if (item is null)
            {
                return;
            }
            try
            {
                await action(item);
            }
            catch (Exception exception)
            {
                failed(exception);
            }
        });
    }
}
