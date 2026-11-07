using System.Collections.Specialized;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.BrowserLogin;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Views;

/// <summary>复用 B 站浏览器登录和 Cookie 验证，管理独立账号配置。</summary>
public sealed class AccountSettingsView : UserControl
{
    private readonly AccountSettingsSurface surface = new();
    private readonly PanelRuntime runtime;
    private readonly IBilibiliAccountSessionService accountSessions;
    private readonly Dictionary<BilibiliAccount, AccountSettingsRowViewModel> rows = [];
    private readonly CancellationTokenSource lifetime = new();
    private bool initialized;
    private bool closed;

    internal Task Initialization { get; private set; } = Task.CompletedTask;

    /// <summary>显示账号编辑器，登录窗口由用户完成身份验证。</summary>
    public AccountSettingsView(PanelRuntime runtime)
        : this(runtime, runtime.AccountSessions)
    {
    }

    internal AccountSettingsView(PanelRuntime runtime, IBilibiliAccountSessionService accountSessions)
    {
        Content = surface;
        this.runtime = runtime;
        this.accountSessions = accountSessions;
        Model = new(() => BrowserLoginAsync(null), () => EditCookieAsync(null), Report);
        surface.DataContext = Model;
        RefreshRows();
        AttachedToVisualTree += OnAttached;
    }

    public AccountSettingsViewModel Model { get; }

    private Window Owner => (Window)TopLevel.GetTopLevel(this)!;

    /// <summary>关闭设置前提交全部账号名称编辑。</summary>
    public async Task SaveEditsAsync()
    {
        foreach (var row in rows.Values)
        {
            row.CommitEdits();
        }
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (initialized)
        {
            return;
        }
        initialized = true;
        Owner.Closed += OnOwnerClosed;
        runtime.Configuration.Accounts.CollectionChanged += OnAccountsChanged;
        accountSessions.Changed += OnAccountStateChanged;
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshRows();
        Initialization = ValidateAccountsAsync();
    }

    private async Task ValidateAccountsAsync()
    {
        try
        {
            foreach (var account in runtime.Configuration.Accounts.ToArray())
            {
                await ValidateAsync(account);
                if (closed)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (closed)
        {
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void OnOwnerClosed(object? sender, EventArgs args)
    {
        closed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        runtime.Configuration.Accounts.CollectionChanged -= OnAccountsChanged;
        accountSessions.Changed -= OnAccountStateChanged;
        Localization.LanguageChanged -= OnLanguageChanged;
        ((Window)sender!).Closed -= OnOwnerClosed;
    }

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RefreshRows();
    }

    private void OnAccountStateChanged(object? sender, string accountId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!closed)
            {
                RefreshStates();
            }
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        RefreshStates();
    }

    private void RefreshRows()
    {
        foreach (var account in rows.Keys.Where(account => !runtime.Configuration.Accounts.Contains(account)).ToArray())
        {
            rows.Remove(account);
        }
        foreach (var account in runtime.Configuration.Accounts)
        {
            if (!rows.ContainsKey(account))
            {
                var row = new AccountSettingsRowViewModel(account, () => BrowserLoginAsync(account), () => SetDefaultAsync(account),
                    () => SaveAndValidateAsync(account), () => DeleteAsync(account), () => EditCookieAsync(account),
                    () => EditCookieAsync(null), Report);
                rows.Add(account, row);
            }
        }
        var ordered = runtime.Configuration.Accounts.Select(account => rows[account]).ToArray();
        foreach (var row in Model.Rows.Where(row => !ordered.Contains(row)).ToArray())
        {
            Model.Rows.Remove(row);
        }
        for (var index = 0; index < ordered.Length; index++)
        {
            var row = ordered[index];
            var previous = Model.Rows.IndexOf(row);
            if (previous < 0)
            {
                Model.Rows.Insert(index, row);
            }
            else if (previous != index)
            {
                Model.Rows.Move(previous, index);
            }
        }
        RefreshStates();
    }

    private void RefreshStates()
    {
        foreach (var (account, row) in rows)
        {
            row.RefreshState(accountSessions.GetState(account.Id),
                runtime.Configuration.Settings.DefaultAccountId == account.Id);
        }
    }

    internal async Task EditCookieAsync(BilibiliAccount? account)
    {
        var dialog = new AccountCookieEditorWindow(account?.Name ?? Localization.Get("Panel.NewAccount"),
            account?.Cookie ?? "", account is null);
        var cookie = await dialog.ShowDialog<string?>(Owner);
        if (closed || cookie is null)
        {
            return;
        }
        if (account is null)
        {
            account = new() { Name = Localization.Get("Panel.NewAccount"), AutoNameOnLogin = true };
            runtime.Configuration.Accounts.Add(account);
        }
        else
        {
            rows[account].CommitEdits();
        }
        account.Cookie = ApiCookieHelper.CreateBilibiliCookie(cookie);
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
        await ValidateAsync(account);
    }

    private async Task SetDefaultAsync(BilibiliAccount account)
    {
        rows[account].CommitEdits();
        runtime.Configuration.Settings.DefaultAccountId = account.Id;
        RefreshStates();
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
    }

    private async Task SaveAndValidateAsync(BilibiliAccount account)
    {
        rows[account].CommitEdits();
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
        await ValidateAsync(account);
    }

    private async Task BrowserLoginAsync(BilibiliAccount? account)
    {
        if (account is not null)
        {
            rows[account].CommitEdits();
        }
        var dialog = new BrowserCookieLoginWindow(BrowserLoginPlatform.Bilibili);
        var result = await dialog.ShowDialog<BrowserCookieLoginResult?>(Owner);
        if (closed || result is null)
        {
            return;
        }
        if (account is null)
        {
            account = new() { Name = Localization.Get("Panel.NewAccount"), AutoNameOnLogin = true };
            runtime.Configuration.Accounts.Add(account);
        }
        var captured = ApiCookieHelper.Parse(result.Cookie);
        var previous = ApiCookieHelper.Parse(account.Cookie);
        foreach (var deviceField in new[] { "buvid4", "buvid_fp" })
        {
            if (!captured.ContainsKey(deviceField) && previous.TryGetValue(deviceField, out var value))
            {
                captured[deviceField] = value;
            }
        }
        account.Cookie = ApiCookieHelper.CreateBilibiliCookie(string.Join(";", captured.Select(pair => $"{pair.Key}={pair.Value}")));
        rows[account].RefreshName();
        if (runtime.Configuration.Settings.DefaultAccountId is null)
        {
            runtime.Configuration.Settings.DefaultAccountId = account.Id;
        }
        RefreshStates();
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
        await ValidateAsync(account);
    }

    private async Task ValidateAsync(BilibiliAccount account)
    {
        if (closed)
        {
            return;
        }
        var credential = account.Cookie;
        var result = await accountSessions.ValidateAsync(account, cancellationToken: lifetime.Token);
        if (closed || account.Cookie != credential || !runtime.Configuration.Accounts.Contains(account))
        {
            return;
        }
        var row = rows[account];
        if (result.Uid > 0 && account.AutoNameOnLogin && !row.HasNameChanges)
        {
            account.Name = result.UserName;
            account.AutoNameOnLogin = false;
            row.RefreshName();
            await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
        }
        RefreshStates();
    }

    private async Task DeleteAsync(BilibiliAccount account)
    {
        if (!await new ConfirmationWindow(Localization.Get("Panel.Delete"),
                Localization.Format("Panel.DeleteAccount", account.Name)).ShowDialog<bool>(Owner))
        {
            return;
        }
        runtime.Configuration.Accounts.Remove(account);
        if (runtime.Configuration.Settings.DefaultAccountId == account.Id)
        {
            runtime.Configuration.Settings.DefaultAccountId = null;
        }
        foreach (var workspace in runtime.Configuration.Workspaces)
        {
            if (workspace.AccountOverrideId == account.Id)
            {
                workspace.AccountOverrideId = null;
            }
            foreach (var room in workspace.Rooms.Where(room => room.AccountOverrideId == account.Id))
            {
                room.AccountOverrideId = null;
            }
        }
        RefreshStates();
        await runtime.ConfigurationStore.SaveAsync(runtime.Configuration);
    }

    private void Report(Exception exception)
    {
        if (closed)
        {
            return;
        }
        Model.Status = AppLogSanitizer.Sanitize(exception.Message);
    }
}
