using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class AccountSettingsRow : UserControl
{
    private readonly AccountSettingsRowSurface surface = new();
    private readonly BilibiliAccount account;
    private readonly TextBox name;
    private readonly RadioButton defaultAccount;

    internal AccountSettingsRow(BilibiliAccount account, Func<Task> login, Func<Task> setDefault,
        Func<Task> validate, Func<Task> delete, Action<Exception> failed)
    {
        Content = surface;
        this.account = account;
        name = surface.FindControl<TextBox>("AccountNameEditor")!;
        name.Text = account.Name;
        defaultAccount = surface.FindControl<RadioButton>("DefaultAccountToggle")!;
        Connect(surface.FindControl<Button>("AccountBrowserLogin")!, login, failed);
        Connect(defaultAccount, setDefault, failed);
        Connect(surface.FindControl<Button>("AccountValidate")!, validate, failed);
        Connect(surface.FindControl<Button>("AccountDelete")!, delete, failed);
    }

    internal bool HasNameChanges => name.Text?.Trim() != account.Name;

    internal void CommitEdits()
    {
        if (!string.IsNullOrWhiteSpace(name.Text))
        {
            if (HasNameChanges)
            {
                account.AutoNameOnLogin = false;
            }
            account.Name = name.Text.Trim();
        }
    }

    internal void RefreshName()
    {
        name.Text = account.Name;
    }

    internal void RefreshState(AccountValidationResult state, bool isDefault)
    {
        var text = Localization.Get($"Panel.AccountStatus.{state.Status}");
        if (state.Uid > 0)
        {
            text += $" · {state.UserName} · UID {state.Uid}";
        }
        var profile = surface.FindControl<TextBlock>("AccountProfile")!;
        profile.Text = text;
        ToolTip.SetTip(profile, text);
        var validity = surface.FindControl<Ellipse>("AccountValidity")!;
        validity.Classes.Set("valid", state.Status == AccountValidationStatus.Valid);
        validity.Classes.Set("invalid", state.Status is AccountValidationStatus.Expired or AccountValidationStatus.Incomplete);
        validity.Classes.Set("uncertain", state.Status == AccountValidationStatus.Unavailable);
        ToolTip.SetTip(validity, text);
        AutomationProperties.SetName(validity, text);
        defaultAccount.IsChecked = isDefault;
    }

    private static void Connect(Button button, Func<Task> action, Action<Exception> failed)
    {
        button.Command = new AsyncRelayCommand(async () =>
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
