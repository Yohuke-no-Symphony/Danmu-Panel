using Avalonia.Controls;
using Avalonia.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.ViewModels.Updates;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views.Updates;

/// <summary>使用共享窗口边框的独立更新提示窗口。</summary>
public sealed class AppUpdateWindow : Window
{
    /// <summary>显示由应用管理生命周期的更新检查状态。</summary>
    public AppUpdateWindow(AppUpdateViewModel model)
    {
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe("Updates.Title"));
        var surface = new AppUpdateSurface { DataContext = model };
        surface.FindControl<Button>("CloseUpdateButton")!.Click += (_, _) => Close();
        Content = surface;
        WindowChrome.AttachFrame(this);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Close();
                args.Handled = true;
            }
        };
    }
}
