using Avalonia.Controls;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels.Workspace;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views;

/// <summary>承载 Neo 样式的房间查找和手动添加弹窗。</summary>
public sealed class RoomEditorWindow : Window
{
    private readonly CancellationTokenSource lifetime = new();
    public RoomEditorViewModel Model { get; }

    /// <summary>关闭时取消搜索；搜索结果与手动资料都通过确认写回。</summary>
    public RoomEditorWindow(PanelRuntime runtime, WorkspaceState workspace, RoomTabState room, bool existing)
        : this(runtime, workspace, room, existing, null)
    {
    }

    internal RoomEditorWindow(PanelRuntime runtime, WorkspaceState workspace, RoomTabState room, bool existing,
        IBilibiliLiveRoomSearchService? searchService)
    {
        Bind(TitleProperty, Localization.Observe(existing ? "Panel.EditRoom" : "Panel.AddRoom"));
        Width = 760;
        Height = 680;
        MinWidth = 480;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Model = new(runtime, workspace, room, existing, result => Close(result), lifetime.Token, searchService);
        Content = new RoomEditorSurface { DataContext = Model };
        Closed += (_, _) => { Model.Dispose(); lifetime.Cancel(); lifetime.Dispose(); };
        WindowChrome.AttachFrame(this);
    }
}
