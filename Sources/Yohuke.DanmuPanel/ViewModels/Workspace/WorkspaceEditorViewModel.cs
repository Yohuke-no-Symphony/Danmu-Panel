using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>管理新建或编辑工作区的独立资料草稿。</summary>
public sealed partial class WorkspaceEditorViewModel : ObservableObject, IDisposable
{
    private readonly PanelRuntime runtime;
    private readonly WorkspaceState workspace;
    private readonly bool creating;
    private readonly Action<bool> closeWindow;
    [ObservableProperty] private string name;
    [ObservableProperty] private AccountChoice? selectedAccount;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool isSaving;
    public ObservableCollection<WorkspaceRoomEditorViewModel> RoomEdits { get; } = [];
    public IReadOnlyList<AccountChoice> AccountChoices { get; }
    public bool IsCreating => creating;
    public string Heading => Localization.Get(creating ? "Panel.AddWorkspace" : "Workspace.Settings");
    public string Subtitle => creating ? Localization.Get("Workspace.CreateHint") : workspace.Name;
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand DeleteWorkspaceCommand { get; }
    public IAsyncRelayCommand<WorkspaceRoomEditorViewModel> DeleteRoomCommand { get; }
    public Func<Task>? ExportRequested { get; set; }
    public Func<RoomTabState, Task<bool>>? DeleteRoomRequested { get; set; }
    public Func<Task<bool>>? DeleteWorkspaceRequested { get; set; }

    /// <summary>复制表单字段并连接窗口结果回传。</summary>
    public WorkspaceEditorViewModel(PanelRuntime runtime, WorkspaceState workspace, bool creating, Action<bool> close)
    {
        this.runtime = runtime;
        this.workspace = workspace;
        this.creating = creating;
        closeWindow = close;
        name = workspace.Name;
        AccountChoices = new[] { new AccountChoice(null, Localization.Get("Panel.InheritAccount")) }
            .Concat(runtime.Configuration.Accounts.Select(value => new AccountChoice(value.Id, value.Name))).ToArray();
        selectedAccount = AccountChoices.FirstOrDefault(value => value.Id == workspace.AccountOverrideId) ?? AccountChoices[0];
        foreach (var room in workspace.Rooms)
        {
            RoomEdits.Add(new(room, AccountChoices));
        }
        SaveCommand = new AsyncRelayCommand(async () =>
        {
            await SaveAsync();
        });
        CancelCommand = new RelayCommand(() => close(false));
        ExportCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (await SaveAsync(false) && ExportRequested is not null)
            {
                await ExportRequested();
            }
        }));
        DeleteWorkspaceCommand = new AsyncRelayCommand(() => RunAsync(async () =>
        {
            if (DeleteWorkspaceRequested is not null && await DeleteWorkspaceRequested())
            {
                closeWindow(true);
            }
        }));
        DeleteRoomCommand = new AsyncRelayCommand<WorkspaceRoomEditorViewModel>(room => room is null ? Task.CompletedTask : DeleteRoomAsync(room));
        Localization.LanguageChanged += OnLanguageChanged;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>验证草稿并保存，成功后可请求关闭窗口。</summary>
    public async Task<bool> SaveAsync(bool close = true)
    {
        if (IsSaving)
        {
            return false;
        }
        IsSaving = true;

        try
        {
            if (string.IsNullOrWhiteSpace(Name))
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
            workspace.Name = Name.Trim();
            workspace.AccountOverrideId = SelectedAccount?.Id;
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
            Error = "";
            if (close)
            {
                closeWindow(true);
            }
            return true;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            return false;
        }
        finally
        {
            IsSaving = false;

        }
    }

    /// <summary>确认删除后移除对应房间草稿。</summary>
    public Task DeleteRoomAsync(WorkspaceRoomEditorViewModel draft)
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
            Error = exception.Message;
        }
    }

}
