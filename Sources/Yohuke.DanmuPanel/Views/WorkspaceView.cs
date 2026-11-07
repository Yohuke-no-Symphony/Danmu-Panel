using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels.Workspace;

namespace Yohuke.DanmuPanel.Views;

/// <summary>采用 Neo 树形交互的工作区侧栏，并提供标题栏监听区域。</summary>
public sealed class WorkspaceView : UserControl, IDisposable
{
    private readonly PanelRuntime runtime;
    private readonly Action<Exception> reportError;
    private readonly IRoomAvatarService avatars;
    private readonly WorkspaceShareService sharing = new();
    private readonly HashSet<WorkspaceState> observedWorkspaces = [];
    private WorkspaceEditorWindow? editor;
    private bool openingEditor;
    private bool disposed;

    public WorkspacePanelViewModel Model { get; }
    public ObservableCollection<WorkspaceTreeItemViewModel> Workspaces => Model.Workspaces;
    public Control Toolbar { get; }
    public WorkspaceState? SelectedWorkspace { get; private set; }
    public RoomTabState? SelectedRoom { get; private set; }
    public ShellWindow HostWindow { get; set; } = null!;
    public event Action<RoomTabState?>? SelectedRoomChanged;

    /// <summary>连接已有配置、阅读选择与监听运行时。</summary>
    public WorkspaceView(PanelRuntime runtime, Action<Exception> reportError, IRoomAvatarService? avatarService = null)
    {
        this.runtime = runtime;
        this.reportError = reportError;
        avatars = avatarService ?? runtime.Avatars;
        AttachedToVisualTree += (_, _) => LoadAvatars();
        Model = new(AddWorkspaceAsync, ImportFromPickerAsync, ToggleListeningAsync,
            item => SelectAsync(item.State), item => AddRoomAsync(item.State), item => EditWorkspaceAsync(item.State),
            async item =>
            {
                await DeleteWorkspaceAsync(item.State);
            }, item => ExportAsync(item.State),
            item => MoveWorkspaceAsync(item.State, -1), item => MoveWorkspaceAsync(item.State, 1),
            item => SelectAsync(item.Workspace, item.State), item => MoveRoomAsync(item.Workspace, item.State, -1),
            item => MoveRoomAsync(item.Workspace, item.State, 1),
            async item =>
            {
                await DeleteRoomAsync(item.Workspace, item.State);
            }, reportError);
        Content = new WorkspaceSidebarSurface { DataContext = Model };
        DataContext = Model;
        Toolbar = new WorkspaceToolbarSurface { DataContext = Model };
        SelectedWorkspace = runtime.Configuration.Workspaces.FirstOrDefault(value => value.Id == runtime.Configuration.Settings.SelectedWorkspaceId)
            ?? runtime.Configuration.Workspaces.FirstOrDefault();
        SelectedRoom = SelectedWorkspace?.Rooms.FirstOrDefault(value => value.Id == SelectedWorkspace.SelectedRoomId)
            ?? SelectedWorkspace?.Rooms.FirstOrDefault();
        runtime.Configuration.Workspaces.CollectionChanged += OnWorkspacesChanged;
        runtime.Changed += OnRuntimeChanged;
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshTree();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        disposed = true;
        runtime.Configuration.Workspaces.CollectionChanged -= OnWorkspacesChanged;
        runtime.Changed -= OnRuntimeChanged;
        Localization.LanguageChanged -= OnLanguageChanged;
        foreach (var workspace in observedWorkspaces)
        {
            workspace.Rooms.CollectionChanged -= OnRoomsChanged;
        }
        observedWorkspaces.Clear();
        foreach (var room in Workspaces.SelectMany(workspace => workspace.Rooms))
        {
            room.Dispose();
        }
    }

    internal async Task SelectAsync(WorkspaceState workspace, RoomTabState? room = null)
    {
        SelectedWorkspace = workspace;
        SelectedRoom = room ?? workspace.Rooms.FirstOrDefault(value => value.Id == workspace.SelectedRoomId) ?? workspace.Rooms.FirstOrDefault();
        await SaveSelectionAsync();
    }

    internal async Task MoveWorkspaceAsync(WorkspaceState workspace, int direction)
    {
        var items = runtime.Configuration.Workspaces;
        var index = items.IndexOf(workspace);
        if (index < 0 || index + direction < 0 || index + direction >= items.Count)
        {
            return;
        }
        items.Move(index, index + direction);
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
    }

    internal async Task MoveRoomAsync(WorkspaceState workspace, RoomTabState room, int direction)
    {
        var index = workspace.Rooms.IndexOf(room);
        if (index < 0 || index + direction < 0 || index + direction >= workspace.Rooms.Count)
        {
            return;
        }
        workspace.Rooms.Move(index, index + direction);
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
    }

    internal async Task ImportAsync(Stream source)
    {
        var workspace = await sharing.ImportAsync(runtime.Configuration.Workspaces, source);
        runtime.Configuration.Workspaces.Add(workspace);
        await SelectAsync(workspace);
    }

    private void RefreshTree()
    {
        var current = runtime.Configuration.Workspaces.ToHashSet();
        foreach (var removed in observedWorkspaces.Except(current).ToArray())
        {
            removed.Rooms.CollectionChanged -= OnRoomsChanged;
            observedWorkspaces.Remove(removed);
        }
        var previous = Workspaces.ToDictionary(item => item.State);
        var removedRooms = Workspaces.SelectMany(workspace => workspace.Rooms).ToHashSet();
        Workspaces.Clear();
        foreach (var workspace in runtime.Configuration.Workspaces)
        {
            if (observedWorkspaces.Add(workspace))
            {
                workspace.Rooms.CollectionChanged += OnRoomsChanged;
            }
            var node = previous.GetValueOrDefault(workspace) ?? new WorkspaceTreeItemViewModel(workspace);
            var rooms = node.Rooms.ToDictionary(item => item.State);
            node.Rooms.Clear();
            foreach (var room in workspace.Rooms)
            {
                var roomNode = rooms.GetValueOrDefault(room) ?? new RoomTreeItemViewModel(workspace, room);
                node.Rooms.Add(roomNode);
                removedRooms.Remove(roomNode);
            }
            Workspaces.Add(node);
        }
        foreach (var room in removedRooms)
        {
            room.Dispose();
        }
        RefreshPresentation();
        if (this.IsAttachedToVisualTree())
        {
            LoadAvatars();
        }
    }

    private void LoadAvatars()
    {
        if (disposed)
        {
            return;
        }
        foreach (var room in Workspaces.SelectMany(workspace => workspace.Rooms))
        {
            _ = LoadAvatarAsync(room);
        }
    }

    private async Task LoadAvatarAsync(RoomTreeItemViewModel room)
    {
        try
        {
            var account = PanelAccountSelector.Resolve(runtime.Configuration, room.Workspace, room.State);
            if (await room.LoadAvatarAsync(avatars, account?.Cookie) && !disposed)
            {
                await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
            }
        }
        catch (Exception exception)
        {
            if (!disposed)
            {
                reportError(exception);
            }
        }
    }

    private void RefreshPresentation()
    {
        if (disposed)
        {
            return;
        }
        foreach (var item in Workspaces)
        {
            item.IsSelected = ReferenceEquals(item.State, SelectedWorkspace);
            item.RoomCountText = Localization.Format("Workspace.RoomCount", item.State.Rooms.Count);
            var account = runtime.Configuration.Accounts.FirstOrDefault(value => value.Id == item.State.AccountOverrideId);
            item.AccountLabel = account?.Name ?? Localization.Get("Panel.InheritAccount");
            foreach (var room in item.Rooms)
            {
                room.IsSelected = ReferenceEquals(room.State, SelectedRoom);
                room.IsListening = runtime.IsListening(room.State) && runtime.GetStatus(room.State) != "Panel.ListenerFailed";
                room.ConnectionStatus = Localization.Get(runtime.GetStatus(room.State));
                room.AvatarPlaceholderText = string.IsNullOrEmpty(room.State.Name) ? "B" : System.Globalization.StringInfo.GetNextTextElement(room.State.Name);
            }
        }
        UpdateToolbar();
    }

    private async Task SaveSelectionAsync()
    {
        runtime.Configuration.Settings.SelectedWorkspaceId = SelectedWorkspace?.Id;
        if (SelectedWorkspace is { } workspace)
        {
            workspace.SelectedRoomId = SelectedRoom?.Id;
        }
        RefreshPresentation();
        SelectedRoomChanged?.Invoke(SelectedRoom);
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
    }

    private async Task AddWorkspaceAsync()
    {
        var workspace = new WorkspaceState();
        if (await HostWindow.ShowModalAsync<bool>(new WorkspaceEditorWindow(runtime, workspace, creating: true)))
        {
            runtime.Configuration.Workspaces.Add(workspace);
            await SelectAsync(workspace);
        }
    }

    internal async Task EditWorkspaceAsync(WorkspaceState workspace)
    {
        if (editor is not null)
        {
            editor.Activate();
            return;
        }
        if (openingEditor)
        {
            return;
        }
        openingEditor = true;
        try
        {
            await SelectAsync(workspace);
            editor = new(runtime, workspace)
            {
                ExportRequested = () => ExportAsync(workspace),
                DeleteRoomRequested = room => DeleteRoomAsync(workspace, room, editor!),
                DeleteWorkspaceRequested = () => DeleteWorkspaceAsync(workspace, editor!)
            };
            await HostWindow.ShowModalAsync<bool>(editor);
        }
        finally
        {
            editor = null;
            openingEditor = false;
            RefreshTree();
        }
    }

    private async Task AddRoomAsync(WorkspaceState workspace)
    {
        var room = new RoomTabState();
        if (await HostWindow.ShowModalAsync<bool>(new RoomEditorWindow(runtime, workspace, room, false)))
        {
            workspace.Rooms.Add(room);
            workspace.IsExpanded = true;
            await SelectAsync(workspace, room);
        }
    }

    private async Task<bool> ConfirmDeleteAsync(string name, Window? owner)
    {
        var dialog = new ConfirmationWindow(Localization.Get("Panel.Delete"), Localization.Format("Panel.DeleteWithHistory", name));
        return owner is null ? await HostWindow.ShowModalAsync<bool>(dialog) : await dialog.ShowDialog<bool>(owner);
    }

    private async Task<bool> DeleteRoomAsync(WorkspaceState workspace, RoomTabState room, Window? owner = null)
    {
        if (!await ConfirmDeleteAsync(room.Name, owner))
        {
            return false;
        }
        await runtime.StopRoomAsync(room);
        await runtime.Messages.ClearRoomAsync(room.Id);
        workspace.Rooms.Remove(room);
        if (ReferenceEquals(SelectedRoom, room))
        {
            SelectedRoom = workspace.Rooms.FirstOrDefault();
        }
        await SaveSelectionAsync();
        return true;
    }

    private async Task<bool> DeleteWorkspaceAsync(WorkspaceState workspace, Window? owner = null)
    {
        if (!await ConfirmDeleteAsync(workspace.Name, owner))
        {
            return false;
        }
        foreach (var room in workspace.Rooms)
        {
            await runtime.StopRoomAsync(room);
            await runtime.Messages.ClearRoomAsync(room.Id);
        }
        runtime.Configuration.Workspaces.Remove(workspace);
        if (ReferenceEquals(SelectedWorkspace, workspace))
        {
            SelectedWorkspace = runtime.Configuration.Workspaces.FirstOrDefault();
            SelectedRoom = SelectedWorkspace?.Rooms.FirstOrDefault(value => value.Id == SelectedWorkspace.SelectedRoomId)
                ?? SelectedWorkspace?.Rooms.FirstOrDefault();
        }
        await SaveSelectionAsync();
        return true;
    }

    private async Task ExportAsync(WorkspaceState workspace)
    {
        var owner = (TopLevel?)editor ?? TopLevel.GetTopLevel(this) ?? HostWindow;
        var name = string.Concat(workspace.Name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var file = await owner.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Localization.Get("Workspace.Export"), SuggestedFileName = name + ".dnworkspace.json",
            FileTypeChoices = [new(Localization.Get("Workspace.FileType")) { Patterns = ["*.dnworkspace.json"] }]
        });
        if (file is not null)
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await sharing.ExportAsync(workspace, stream);
        }
    }

    private async Task ImportFromPickerAsync()
    {
        var owner = TopLevel.GetTopLevel(this) ?? HostWindow;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Localization.Get("Workspace.Import"), AllowMultiple = false,
            FileTypeFilter = [new(Localization.Get("Workspace.FileType")) { Patterns = ["*.dnworkspace.json", "*.json"] }]
        });
        if (files.FirstOrDefault() is { } file)
        {
            await using var stream = await file.OpenReadAsync();
            await ImportAsync(stream);
        }
    }

    private async Task ToggleListeningAsync()
    {
        if (SelectedWorkspace is { } workspace && SelectedRoom is { } room)
        {
            await runtime.ToggleListeningAsync(workspace, room);
        }
        RefreshPresentation();
    }

    private async void Execute(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            reportError(exception);
        }
    }

    internal void Workspace_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WorkspaceTreeItemViewModel item)
        {
            item.State.IsExpanded = !item.State.IsExpanded;
            Execute(() => runtime.ConfigurationStore.SaveAsync(runtime.Configuration));
        }
    }
    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshTree();
    private void OnRoomsChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshTree();
    private void OnRuntimeChanged(string roomId) => Dispatcher.UIThread.Post(RefreshPresentation);
    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshPresentation();

    private void UpdateToolbar()
    {
        var room = SelectedRoom;
        Model.RoomTitle = room is null ? Localization.Get("Panel.SelectRoom") : room.Name;
        Model.RoomNumber = room?.RoomId ?? "";
        Model.ConnectionStatus = Localization.Get(runtime.GetStatus(room));
        Model.IsListening = room is not null && runtime.IsListening(room) && runtime.GetStatus(room) != "Panel.ListenerFailed";
        Model.ListenIcon = room is not null && runtime.IsListening(room) ? LucideIconKind.CircleStop : LucideIconKind.Radio;
        Model.ListenText = Localization.Get(room is not null && runtime.IsListening(room) ? "Panel.StopListening" : "Panel.StartListening");
        Model.HasRoom = room is not null;
    }
}
