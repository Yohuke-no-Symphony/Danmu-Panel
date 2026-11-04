using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services.Updates;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views.About;

/// <summary>显示应用构建信息、项目来源及许可的独立关于窗口。</summary>
public sealed class AboutWindow : Window
{
    public string ApplicationName { get; } = typeof(App).Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
    public string Publisher { get; } = typeof(App).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company;
    public string CurrentVersion { get; } = AppUpdateVersion.Current.ToString();

    /// <summary>使用共享窗口边框创建可通过关闭按钮或 Esc 退出的关于页。</summary>
    public AboutWindow()
    {
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe("About.Title"));
        var surface = new AboutSurface { DataContext = this };
        surface.FindControl<Button>("CloseAboutButton")!.Click += (_, _) => Close();
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
