using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
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
        var surface = new AccountCookieEditorSurface();
        surface.FindControl<TextBlock>("AccountCaption")!.Text = accountName;
        var input = surface.FindControl<TextBox>("AccountCookieInput")!;
        input.Text = cookie;
        surface.FindControl<Button>("CancelCookieEdit")!.Command = new RelayCommand(() => Close(null));
        surface.FindControl<Button>("SaveCookieEdit")!.Command = new RelayCommand(() => Close(input.Text ?? ""));
        Content = surface;
        WindowChrome.AttachFrame(this);
    }
}
