using Avalonia.Controls;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>仅在确认后返回手动 Cookie，不直接修改账号。</summary>
public sealed class AccountCookieEditorWindow : Window
{
    /// <summary>为指定账号或新账号创建手动 Cookie 输入窗口。</summary>
    public AccountCookieEditorWindow(string accountName, string cookie, bool adding)
    {
        Width = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(TitleProperty, Localization.Observe(adding ? "Panel.AddAccountManually" : "Panel.EditAccountCookie"));
        Content = new AccountCookieEditorSurface
        {
            DataContext = new AccountCookieEditorViewModel(accountName, cookie, value => Close(value))
        };
        WindowChrome.AttachFrame(this);
    }
}
