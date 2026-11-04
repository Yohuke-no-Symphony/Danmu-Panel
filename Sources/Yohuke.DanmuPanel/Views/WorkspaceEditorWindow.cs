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
    private readonly PanelRuntime runtime;
    private readonly WorkspaceState workspace;
    private readonly bool creating;
    private readonly UserControl surface;
    private readonly TextBox name;
    private readonly ComboBox account;
    private readonly TextBlock error;
    private bool closeApproved;
    private bool saving;

    public ObservableCollection<WorkspaceRoomEditorViewModel> RoomEdits { get; } = [];
    public Func<Task>? ExportRequested { get; set; }
    public Func<RoomTabState, Task<bool>>? DeleteRoomRequested { get; set; }
    public Func<Task<bool>>? DeleteWorkspaceRequested { get; set; }

    /// <summary>编辑工作区及房间资料；新建时取消不会写入配置。</summary>
    public WorkspaceEditorWindow(PanelRuntime runtime, WorkspaceState workspace, bool creating = false)
    {
        this.runtime = runtime;
        this.workspace = workspace;
        this.creating = creating;
        surface = creating ? new WorkspaceCreateSurface() : new WorkspaceEditorSurface();
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
        surface.DataContext = this;
        name = surface.FindControl<TextBox>("WorkspaceNameInput")!;
        account = creating ? new ComboBox() : surface.FindControl<ComboBox>("WorkspaceAccountInput")!;
        error = surface.FindControl<TextBlock>("ErrorText")!;
        name.Text = workspace.Name;
        var choices = new[] { new AccountChoice(null, Localization.Get("Panel.InheritAccount")) }
            .Concat(runtime.Configuration.Accounts.Select(value => new AccountChoice(value.Id, value.Name))).ToArray();
        account.ItemsSource = choices;
        account.SelectedItem = choices.FirstOrDefault(value => value.Id == workspace.AccountOverrideId) ?? choices[0];
        foreach (var room in workspace.Rooms)
        {
            RoomEdits.Add(new(room, choices));
        }
        surface.FindControl<TextBlock>("Heading")!.Bind(TextBlock.TextProperty, Localization.Observe(creating ? "Panel.AddWorkspace" : "Workspace.Settings"));
        if (creating)
        {
            surface.FindControl<TextBlock>("Subtitle")!.Bind(TextBlock.TextProperty, Localization.Observe("Workspace.CreateHint"));
        }
        else
        {
            surface.FindControl<TextBlock>("Subtitle")!.Text = workspace.Name;
        }
        surface.FindControl<Button>("SaveButton")!.Click += async (_, _) => await SaveAsync();
        var cancel = surface.FindControl<Button>("CancelButton")!;
        cancel.IsVisible = creating;
        cancel.Click += (_, _) => { closeApproved = true; Close(false); };
        if (!creating)
        {
            var export = surface.FindControl<Button>("ExportButton")!;
            export.Click += async (_, _) => await RunAsync(async () =>
            {
                if (await SaveAsync(false) && ExportRequested is not null)
                {
                    await ExportRequested();
                }
            });
            var delete = surface.FindControl<Button>("DeleteWorkspaceButton")!;
            delete.Click += async (_, _) => await RunAsync(async () =>
            {
                if (DeleteWorkspaceRequested is not null && await DeleteWorkspaceRequested())
                {
                    closeApproved = true;
                    Close(true);
                }
            });
        }
        Content = surface;
        WindowChrome.AttachFrame(this);
        Closing += OnClosing;
    }

    internal async Task<bool> SaveAsync(bool close = true)
    {
        if (saving)
        {
            return false;
        }
        saving = true;
        surface.IsEnabled = false;
        try
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                throw new InvalidOperationException(Localization.Get("Panel.NameRequired"));
            }
            foreach (var draft in RoomEdits)
            {
                if (!string.IsNullOrWhiteSpace(draft.OwnerUid) && !WorkspaceShareService.IsPositiveId(draft.OwnerUid.Trim()))
                {
                    throw new InvalidOperationException(Localization.Get("Workspace.InvalidOwnerUid"));
                }
            }
            workspace.Name = name.Text.Trim();
            workspace.AccountOverrideId = (account.SelectedItem as AccountChoice)?.Id;
            foreach (var draft in RoomEdits)
            {
                draft.State.Name = string.IsNullOrWhiteSpace(draft.Name) ? draft.State.RoomId : draft.Name.Trim();
                draft.State.OwnerUid = draft.OwnerUid.Trim();
                draft.State.AccountOverrideId = draft.SelectedAccount?.Id;
            }
            if (!creating)
            {
                await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
            }
            error.Text = "";
            if (close)
            {
                closeApproved = true;
                Close(true);
            }
            return true;
        }
        catch (Exception exception)
        {
            error.Text = exception.Message;
            return false;
        }
        finally
        {
            saving = false;
            surface.IsEnabled = true;
        }
    }

    internal Task DeleteRoomAsync(WorkspaceRoomEditorViewModel draft)
    {
        return RunAsync(async () =>
        {
            if (DeleteRoomRequested is not null && await DeleteRoomRequested(draft.State))
            {
                RoomEdits.Remove(draft);
            }
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            error.Text = exception.Message;
        }
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
