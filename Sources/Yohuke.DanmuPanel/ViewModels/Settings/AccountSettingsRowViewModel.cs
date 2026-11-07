using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>管理账号行名称草稿、验证状态和独立操作。</summary>
public sealed partial class AccountSettingsRowViewModel : ObservableObject
{
    private readonly BilibiliAccount account;
    [ObservableProperty] private string name;
    [ObservableProperty] private string profile = "";
    [ObservableProperty] private bool isDefault;
    [ObservableProperty] private bool isValid;
    [ObservableProperty] private bool isInvalid;
    [ObservableProperty] private bool isUncertain;

    public IAsyncRelayCommand LoginCommand { get; }
    public IAsyncRelayCommand SetDefaultCommand { get; }
    public IAsyncRelayCommand ValidateCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand EditCookieCommand { get; }
    public IAsyncRelayCommand AddManuallyCommand { get; }
    internal bool HasNameChanges => Name?.Trim() != account.Name;

    /// <summary>复制名称并连接对应账号的操作。</summary>
    public AccountSettingsRowViewModel(BilibiliAccount account, Func<Task> login, Func<Task> setDefault,
        Func<Task> validate, Func<Task> delete, Func<Task> editCookie, Func<Task> addManually, Action<Exception> failed)
    {
        this.account = account;
        name = account.Name;
        LoginCommand = Connect(login, failed);
        SetDefaultCommand = Connect(setDefault, failed);
        ValidateCommand = Connect(validate, failed);
        DeleteCommand = Connect(delete, failed);
        EditCookieCommand = Connect(editCookie, failed);
        AddManuallyCommand = Connect(addManually, failed);
    }

    internal void CommitEdits()
    {
        if (!string.IsNullOrWhiteSpace(Name))
        {
            if (HasNameChanges)
            {
                account.AutoNameOnLogin = false;
            }
            account.Name = Name.Trim();
        }
    }

    internal void RefreshName()
    {
        Name = account.Name;
    }

    internal void RefreshState(AccountValidationResult state, bool defaultAccount)
    {
        Profile = Localization.Get($"Panel.AccountStatus.{state.Status}");
        if (state.Uid > 0)
        {
            Profile += $" · {state.UserName} · UID {state.Uid}";
        }
        IsValid = state.Status == AccountValidationStatus.Valid;
        IsInvalid = state.Status is AccountValidationStatus.Expired or AccountValidationStatus.Incomplete;
        IsUncertain = state.Status == AccountValidationStatus.Unavailable;
        IsDefault = defaultAccount;
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
}
