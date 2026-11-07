using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yohuke.DanmuPanel.ViewModels.Workspace;

/// <summary>管理保存布局时的名称草稿。</summary>
public sealed partial class ShellLayoutNameViewModel : ObservableObject
{
    [ObservableProperty] private string name = "";
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>仅将非空名称作为确认结果返回。</summary>
    public ShellLayoutNameViewModel(Action<string?> close)
    {
        SaveCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                close(Name.Trim());
            }
        });
        CancelCommand = new RelayCommand(() => close(null));
    }
}
