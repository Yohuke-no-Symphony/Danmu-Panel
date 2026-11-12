using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Workspace;

/// <summary>验证树菜单、资料编辑和排序的真实界面行为。</summary>
public sealed class WorkspaceInteractionTests
{
    /// <summary>窗口打开过程重复请求仍只编辑首次指定的工作区。</summary>
    [AvaloniaFact]
    public async Task OpeningClickedWorkspaceCannotReenterDuringInitialSave()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var view = new WorkspaceView(runtime, exception => throw exception, new FakeRoomAvatarService());
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, _ => (Control)new Border());
        panels[WorkbenchPanelIds.WORKSPACE] = view;
        var shell = new ShellWindow(panels, view.Toolbar, directory.Path);
        view.HostWindow = shell;
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            var clicked = runtime.Configuration.Workspaces[1];
            var opening = view.EditWorkspaceAsync(clicked);
            await view.EditWorkspaceAsync(runtime.Configuration.Workspaces[0]);
            for (var attempt = 0; attempt < 100 && !shell.OwnedWindows.OfType<WorkspaceEditorWindow>().Any(); attempt++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }
            var editor = Assert.Single(shell.OwnedWindows.OfType<WorkspaceEditorWindow>());
            Pump(editor);
            Assert.Same(clicked, view.SelectedWorkspace);
            Assert.Equal(clicked.Name, editor.GetVisualDescendants().OfType<TextBox>().Single(text => text.Name == "WorkspaceNameInput").Text);
            await editor.SaveAsync();
            await opening;
            Assert.DoesNotContain(shell.OwnedWindows, window => window is WorkspaceEditorWindow);
        }
        finally
        {
            foreach (var editor in shell.OwnedWindows.OfType<WorkspaceEditorWindow>().ToArray())
            {
                await editor.SaveAsync();
            }
            await shell.CloseAsync();
        }
    }

    /// <summary>树形菜单保持选择对象并将调整后的顺序落盘。</summary>
    [AvaloniaFact]
    public async Task TreeUsesContextMenusAndPersistsOrderingWithoutChangingSelection()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        using var view = new WorkspaceView(runtime, exception => throw exception, new FakeRoomAvatarService());
        var window = new Window { Width = 360, Height = 600, Content = view };
        try
        {
            window.Show();
            Pump(window);
            Assert.Empty(view.GetVisualDescendants().OfType<ComboBox>());
            Assert.Equal(2, view.GetVisualDescendants().OfType<Button>().Count(button => button.Classes.Contains("treeWorkspaceButton")));
            var surface = view.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "WorkspaceSurface");
            Assert.Equal(2, surface.ContextMenu!.Items.OfType<MenuItem>().Count());
            var first = runtime.Configuration.Workspaces[0];
            var second = runtime.Configuration.Workspaces[1];
            await view.SelectAsync(first, first.Rooms[1]);
            var selected = view.SelectedRoom;
            await view.MoveWorkspaceAsync(second, -1);
            await view.MoveRoomAsync(first, first.Rooms[1], -1);
            Assert.Same(first, view.SelectedWorkspace);
            Assert.Same(selected, view.SelectedRoom);
            var loaded = runtime.ConfigurationStore.Load();
            Assert.Equal(second.Id, loaded.Workspaces[0].Id);
            Assert.Equal(selected!.Id, loaded.Workspaces[1].Rooms[0].Id);
            Pump(window);
            var row = view.GetVisualDescendants().OfType<Grid>().First(grid => grid.Classes.Contains("workspaceTreeRow"));
            Assert.Equal(7, row.ContextMenu!.Items.OfType<MenuItem>().Count());
            Assert.All(row.ContextMenu.Items.OfType<MenuItem>(), item => Assert.NotNull(item.Icon));
            Pump(window);
            Capture(window, "workspace-tree");
            row.ContextMenu.Open(row);
            Pump(window);
            Capture(window, "workspace-context-menu");
            row.ContextMenu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>工作区资料保存保留房间标签身份和房号。</summary>
    [AvaloniaFact]
    public async Task WorkspaceEditorSavesRoomDraftsAndKeepsRoomIdentity()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var workspace = runtime.Configuration.Workspaces[0];
        var room = workspace.Rooms[0];
        var editor = new WorkspaceEditorWindow(runtime, workspace);
        try
        {
            editor.Show();
            Pump(editor);
            editor.RoomEdits[0].Name = "新名字";
            editor.RoomEdits[0].OwnerUid = "789";
            var id = room.Id;
            var roomId = room.RoomId;
            Capture(editor, "workspace-settings");
            Assert.True(await editor.SaveAsync());
            Assert.False(editor.IsVisible);
            var loaded = runtime.ConfigurationStore.Load().Workspaces[0].Rooms[0];
            Assert.Equal(id, loaded.Id);
            Assert.Equal(roomId, loaded.RoomId);
            Assert.Equal("新名字", loaded.Name);
            Assert.Equal("789", loaded.OwnerUid);
        }
        finally
        {
            editor.Close();
        }
    }

    /// <summary>保存失败保持设置窗口打开，恢复存储后可重试。</summary>
    [AvaloniaFact]
    public async Task SaveFailureKeepsWorkspaceEditorOpenForRetry()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var editor = new WorkspaceEditorWindow(runtime, runtime.Configuration.Workspaces[0]);
        var obstruction = Path.Combine(directory.Path, "settings.json.tmp");
        try
        {
            editor.Show();
            Directory.CreateDirectory(obstruction);
            Assert.False(await editor.SaveAsync());
            Assert.True(editor.IsVisible);
            Directory.Delete(obstruction);
            Assert.True(await editor.SaveAsync());
            Assert.False(editor.IsVisible);
        }
        finally
        {
            if (Directory.Exists(obstruction))
            {
                Directory.Delete(obstruction);
            }
            editor.Close();
        }
    }

    /// <summary>取消新建和非法房号不会修改配置，并输出可选预览。</summary>
    [AvaloniaFact]
    public async Task NewWorkspaceCancelAndRoomValidationDoNotMutateConfiguration()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = CreateRuntime(directory);
        var draft = new WorkspaceState();
        var add = new WorkspaceEditorWindow(runtime, draft, creating: true);
        var room = new RoomTabState();
        var roomEditor = new RoomEditorWindow(runtime, runtime.Configuration.Workspaces[0], room, false);
        try
        {
            add.Show();
            Pump(add);
            Capture(add, "new-workspace");
            var cancel = add.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CancelButton");
            cancel.Command!.Execute(null);
            Assert.False(add.IsVisible);
            Assert.Equal("", draft.Name);
            Assert.Equal(2, runtime.Configuration.Workspaces.Count);
            roomEditor.Show();
            roomEditor.Model.LiveRoomSearchResults.Add(new(new() { OwnerName = "夜更音乐电台", RoomId = "123456", OwnerUid = "789", Title = "今晚一起听歌", IsLive = true }));
            Pump(roomEditor);
            Capture(roomEditor, "add-room");
            roomEditor.Model.NewRoomId = "invalid";
            roomEditor.Model.ConfirmCommand.Execute(null);
            Assert.True(roomEditor.IsVisible);
            Assert.Equal("", room.RoomId);
            roomEditor.Model.NewRoomId = "123";
            roomEditor.Model.NewRoomName = "New room";
            roomEditor.Model.NewRoomOwnerUid = "456";
            roomEditor.Model.ConfirmCommand.Execute(null);
            Assert.False(roomEditor.IsVisible);
            Assert.Equal("123", room.RoomId);
            Assert.Equal("456", room.OwnerUid);
        }
        finally
        {
            add.Close();
            roomEditor.Close();
        }
    }

    private static PanelRuntime CreateRuntime(MessageStoreTestDirectory directory)
    {
        return new(new(directory.Path), new()
        {
            Workspaces =
            [
                new() { Name = "直播工作区", Rooms = [new() { Name = "夜更音乐电台", RoomId = "123456", OwnerUid = "456" }, new() { Name = "绘画直播", RoomId = "456789" }] },
                new() { Name = "周末工作区", Rooms = [new() { Name = "周末剧场", RoomId = "987654" }] }
            ]
        });
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_WORKSPACE_SCREENSHOTS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(directory, name + ".png"));
        }
    }
}
