using System.Collections.ObjectModel;
using Avalonia.Controls;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels.Workspace;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views;

/// <summary>承载工作区资料草稿，保存成功后才允许关闭设置窗口。</summary>
public sealed class WorkspaceEditorWindow : Window
{
    private readonly bool creating;
    private bool closeApproved;
    public WorkspaceEditorViewModel Model { get; }
    public ObservableCollection<WorkspaceRoomEditorViewModel> RoomEdits => Model.RoomEdits;
    public Func<Task>? ExportRequested
    {
        get => Model.ExportRequested;
        set => Model.ExportRequested = value;
    }
    public Func<RoomTabState, Task<bool>>? DeleteRoomRequested
    {
        get => Model.DeleteRoomRequested;
        set => Model.DeleteRoomRequested = value;
    }
    public Func<Task<bool>>? DeleteWorkspaceRequested
    {
        get => Model.DeleteWorkspaceRequested;
        set => Model.DeleteWorkspaceRequested = value;
    }

    /// <summary>编辑工作区及房间资料；新建时取消不会写入配置。</summary>
    public WorkspaceEditorWindow(PanelRuntime runtime, WorkspaceState workspace, bool creating = false)
    {
        this.creating = creating;
        Model = new(runtime, workspace, creating, result =>
        {
            closeApproved = true;
            Close(result);
        });
        Width = creating ? 600 : 800;
        Height = creating ? 280 : 740;
        MinWidth = 440;
        MinHeight = creating ? 0 : 340;
        if (creating)
        {
            SizeToContent = SizeToContent.Height;
            CanResize = false;
        }
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe(creating ? "Panel.AddWorkspace" : "Workspace.Settings"));
        Content = creating ? new WorkspaceCreateSurface { DataContext = Model } : new WorkspaceEditorSurface { DataContext = Model };
        WindowChrome.AttachFrame(this);
        Closing += OnClosing;
        Closed += (_, _) => Model.Dispose();
    }

    internal Task<bool> SaveAsync(bool close = true)
    {
        return Model.SaveAsync(close);
    }

    internal Task DeleteRoomAsync(WorkspaceRoomEditorViewModel draft)
    {
        return Model.DeleteRoomAsync(draft);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeApproved || creating)
        {
            return;
        }
        e.Cancel = true;
        await SaveAsync();
    }
}
