using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views;

/// <summary>明确显示操作范围的 Neo 样式确认窗口。</summary>
public sealed class ConfirmationWindow : Window
{
    public string DeleteDialogTitle { get; }
    public string DeleteDialogDescription { get; }
    public string DeleteDialogPrimaryText => Localization.Get("Panel.Confirm");
    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>创建具有确认与取消操作的模态窗口。</summary>
    public ConfirmationWindow(string title, string detail)
    {
        Title = title;
        DeleteDialogTitle = title;
        DeleteDialogDescription = detail;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ConfirmCommand = new RelayCommand(() => Close(true));
        CancelCommand = new RelayCommand(() => Close(false));
        Content = new ConfirmationSurface { DataContext = this };
        WindowChrome.AttachFrame(this);
    }
}
