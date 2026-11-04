using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
    private readonly WorkspaceShareService sharing = new();
    private readonly HashSet<WorkspaceState> observedWorkspaces = [];
    private readonly TextBlock roomTitle = new();
    private readonly Border statusLight = new();
    private readonly TextBlock roomNumber = new();
    private readonly TextBlock listenText = new();
    private readonly LucideIcon listenIcon = new() { Size = 16 };
    private readonly Button listen;
    private WorkspaceEditorWindow? editor;
    private bool openingEditor;
    private bool disposed;

    public ObservableCollection<WorkspaceTreeItemViewModel> Workspaces { get; } = [];
    public Control Toolbar { get; }
    public WorkspaceState? SelectedWorkspace { get; private set; }
    public RoomTabState? SelectedRoom { get; private set; }
    public ShellWindow HostWindow { get; set; } = null!;
    public event Action<RoomTabState?>? SelectedRoomChanged;

    /// <summary>连接已有配置、阅读选择与监听运行时。</summary>
    public WorkspaceView(PanelRuntime runtime, Action<Exception> reportError)
    {
        this.runtime = runtime;
        this.reportError = reportError;
        Content = new WorkspaceSidebarSurface { DataContext = this };
        DataContext = this;
        listen = Ui.Button("Panel.StartListening", ToggleListeningAsync, reportError);
        listen.Classes.Add("listenToggleButton");
        listen.Height = 32;
        listen.MinHeight = 32;
        listen.Padding = new(10, 4);
        listen.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { listenIcon, listenText } };
        statusLight.Classes.Add("listenStatusLight");
        roomNumber.Classes.Add("muted");
        roomTitle.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        roomTitle.MaxWidth = 240;
        roomTitle.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        Toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children = { statusLight, roomTitle, roomNumber, listen }
        };
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
                node.Rooms.Add(rooms.GetValueOrDefault(room) ?? new RoomTreeItemViewModel(workspace, room));
            }
            Workspaces.Add(node);
        }
        RefreshPresentation();
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

    internal void AddWorkspace_OnClick(object? sender, RoutedEventArgs e) => Execute(AddWorkspaceAsync);
    internal void Import_OnClick(object? sender, RoutedEventArgs e) => Execute(ImportFromPickerAsync);
    internal void SelectWorkspace_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WorkspaceTreeItemViewModel item)
        {
            Execute(() => SelectAsync(item.State));
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
    internal void SelectRoom_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is RoomTreeItemViewModel item)
        {
            Execute(() => SelectAsync(item.Workspace, item.State));
        }
    }
    internal void AddRoom_OnClick(object? sender, RoutedEventArgs e) => WithWorkspace(sender, AddRoomAsync);
    internal void EditWorkspace_OnClick(object? sender, RoutedEventArgs e) => WithWorkspace(sender, EditWorkspaceAsync);
    internal void DeleteWorkspace_OnClick(object? sender, RoutedEventArgs e) => WithWorkspace(sender, async workspace => { await DeleteWorkspaceAsync(workspace); });
    internal void Export_OnClick(object? sender, RoutedEventArgs e) => WithWorkspace(sender, ExportAsync);
    internal void WorkspaceUp_OnClick(object? sender, RoutedEventArgs e) => WithWorkspace(sender, workspace => MoveWorkspaceAsync(workspace, -1));
    internal void WorkspaceDown_OnClick(object? sender, RoutedEventArgs e) => WithWorkspace(sender, workspace => MoveWorkspaceAsync(workspace, 1));
    internal void RoomUp_OnClick(object? sender, RoutedEventArgs e) => WithRoom(sender, item => MoveRoomAsync(item.Workspace, item.State, -1));
    internal void RoomDown_OnClick(object? sender, RoutedEventArgs e) => WithRoom(sender, item => MoveRoomAsync(item.Workspace, item.State, 1));
    internal void DeleteRoom_OnClick(object? sender, RoutedEventArgs e) => WithRoom(sender, async item => { await DeleteRoomAsync(item.Workspace, item.State); });
    private void WithWorkspace(object? sender, Func<WorkspaceState, Task> action)
    {
        if ((sender as Control)?.DataContext is WorkspaceTreeItemViewModel item)
        {
            Execute(() => action(item.State));
        }
    }
    private void WithRoom(object? sender, Func<RoomTreeItemViewModel, Task> action)
    {
        if ((sender as Control)?.DataContext is RoomTreeItemViewModel item)
        {
            Execute(() => action(item));
        }
    }
    private void OnWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshTree();
    private void OnRoomsChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshTree();
    private void OnRuntimeChanged(string roomId) => Dispatcher.UIThread.Post(RefreshPresentation);
    private void OnLanguageChanged(object? sender, EventArgs args) => RefreshPresentation();

    private void UpdateToolbar()
    {
        var room = SelectedRoom;
        roomTitle.Text = room is null ? Localization.Get("Panel.SelectRoom") : room.Name;
        roomNumber.Text = room?.RoomId ?? "";
        var listening = room is not null && runtime.IsListening(room) && runtime.GetStatus(room) != "Panel.ListenerFailed";
        ToolTip.SetTip(statusLight, Localization.Get(runtime.GetStatus(room)));
        ToolTip.SetTip(listen, Localization.Get(runtime.GetStatus(room)));
        statusLight.Classes.Set("listening", listening);
        statusLight.Classes.Set("idle", !listening);
        listen.Classes.Set("listening", listening);
        listen.Classes.Set("idle", !listening);
        listenIcon.Kind = room is not null && runtime.IsListening(room) ? LucideIconKind.CircleStop : LucideIconKind.Radio;
        listenText.Text = Localization.Get(room is not null && runtime.IsListening(room) ? "Panel.StopListening" : "Panel.StartListening");
        listen.IsEnabled = room is not null;
    }
}
