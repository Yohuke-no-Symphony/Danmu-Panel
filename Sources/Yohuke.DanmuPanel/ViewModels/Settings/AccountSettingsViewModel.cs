using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>管理账号列表、页面提示和新增操作。</summary>
public sealed partial class AccountSettingsViewModel : ObservableObject
{
    public ObservableCollection<AccountSettingsRowViewModel> Rows { get; } = [];
    public IAsyncRelayCommand AddAccountCommand { get; }
    public IAsyncRelayCommand AddManuallyCommand { get; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string status = "";
    public bool HasStatus => Status.Length > 0;

    /// <summary>连接账号窗口的登录及手动录入流程。</summary>
    public AccountSettingsViewModel(Func<Task> addAccount, Func<Task> addManually, Action<Exception> failed)
    {
        AddAccountCommand = Connect(addAccount, failed);
        AddManuallyCommand = Connect(addManually, failed);
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
