using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>保存确认前独立的账号 Cookie 草稿。</summary>
public sealed partial class AccountCookieEditorViewModel : ObservableObject
{
    public string AccountName { get; }
    [ObservableProperty] private string cookie;
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>连接确认及取消结果，不修改账号配置。</summary>
    public AccountCookieEditorViewModel(string accountName, string cookie, Action<string?> close)
    {
        AccountName = accountName;
        this.cookie = cookie;
        SaveCommand = new RelayCommand(() => close(Cookie ?? ""));
        CancelCommand = new RelayCommand(() => close(null));
    }
}
