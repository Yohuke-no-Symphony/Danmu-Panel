using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Views.Settings;
using Microsoft.Data.Sqlite;
using Xunit;
using Yohuke.DanmuCore.Apis;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Tests.Settings;

public sealed class AccountSettingsLifecycleTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingManualCookieDoesNotChangeOrCreateAccount(bool adding)
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-cookie-cancel-" + Guid.NewGuid().ToString("N"));
        var account = new BilibiliAccount { Name = "original", Cookie = "SESSDATA=original" };
        var runtime = new PanelRuntime(new(directory), new() { Accounts = [account] });
        var view = new AccountSettingsView(runtime);
        var window = new Window { Content = view, Width = 740, Height = 760 };
        try
        {
            window.Show();
            await view.Initialization;
            var editing = view.EditCookieAsync(adding ? null : account);
            var dialog = Assert.IsType<AccountCookieEditorWindow>(Assert.Single(window.OwnedWindows));
            dialog.GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "AccountCookieInput").Text = "SESSDATA=cancelled";
            dialog.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "CancelCookieEdit")
                .Command!.Execute(null);
            await editing;
            Assert.Same(account, Assert.Single(runtime.Configuration.Accounts));
            Assert.Equal("SESSDATA=original", account.Cookie);
            Assert.False(File.Exists(Path.Combine(directory, "accounts.json")));
        }
        finally
        {
            window.Close();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task CookieContextActionSavesOnlyTheAccountWhoseRowWasClicked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-cookie-target-" + Guid.NewGuid().ToString("N"));
        var first = new BilibiliAccount { Name = "first", Cookie = "SESSDATA=first" };
        var second = new BilibiliAccount { Name = "second", Cookie = "SESSDATA=second" };
        var runtime = new PanelRuntime(new(directory), new() { Accounts = [first, second] });
        var view = new AccountSettingsView(runtime);
        var window = new Window { Content = view, Width = 740, Height = 760 };
        try
        {
            window.Show();
            await view.Initialization;
            var row = view.GetVisualDescendants().OfType<AccountSettingsRow>().Last();
            var edit = Assert.IsType<MenuItem>(row.ContextMenu!.Items[0]);
            var editing = Assert.IsAssignableFrom<IAsyncRelayCommand>(edit.Command).ExecuteAsync(null);
            var dialog = Assert.IsType<AccountCookieEditorWindow>(Assert.Single(window.OwnedWindows));
            dialog.GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "AccountCookieInput").Text = "SESSDATA=second-edited";
            dialog.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "SaveCookieEdit")
                .Command!.Execute(null);
            await editing;
            var restored = runtime.ConfigurationStore.Load();
            Assert.Equal("first", ApiCookieHelper.GetValue(restored.Accounts[0].Cookie, "SESSDATA"));
            Assert.Equal("second-edited", ApiCookieHelper.GetValue(restored.Accounts[1].Cookie, "SESSDATA"));
        }
        finally
        {
            window.Close();
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task AutomaticNameAfterValidationPreservesCookieDraft()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-account-draft-" + Guid.NewGuid().ToString("N"));
        var account = new BilibiliAccount { Name = "new account", AutoNameOnLogin = true };
        var runtime = new PanelRuntime(new(directory), new() { Accounts = [account] });
        var validation = new ControlledAccountValidationService();
        using var sessions = new BilibiliAccountSessionService(validation);
        using var binding = new BilibiliAccountSessionBinding(sessions);
        binding.Register(account);
        var view = new AccountSettingsView(runtime, sessions);
        var window = new Window { Content = view, Width = 740, Height = 760 };
        try
        {
            window.Show();
            var editing = view.EditCookieAsync(account);
            var dialog = Assert.IsType<AccountCookieEditorWindow>(Assert.Single(window.OwnedWindows));
            var cookie = dialog.GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "AccountCookieInput");
            cookie.Text = "SESSDATA=edited-during-validation";
            validation.Complete(new() { Status = AccountValidationStatus.Valid, Uid = 42, UserName = "validated name" });
            await view.Initialization;
            Assert.Equal("validated name", account.Name);
            Assert.Equal("SESSDATA=edited-during-validation", cookie.Text);
            dialog.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "SaveCookieEdit")
                .Command!.Execute(null);
            await editing;
            Assert.Equal("edited-during-validation", ApiCookieHelper.GetValue(
                Assert.Single(runtime.ConfigurationStore.Load().Accounts).Cookie, "SESSDATA"));
        }
        finally
        {
            validation.Complete(new() { Status = AccountValidationStatus.NotBound });
            window.Close();
            await view.Initialization;
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task ValidationCompletedAfterCloseCannotRenameOrSaveAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), "panel-account-close-" + Guid.NewGuid().ToString("N"));
        var account = new BilibiliAccount { Name = "original", AutoNameOnLogin = true };
        var runtime = new PanelRuntime(new(directory), new() { Accounts = [account] });
        var validation = new ControlledAccountValidationService();
        using var sessions = new BilibiliAccountSessionService(validation);
        using var binding = new BilibiliAccountSessionBinding(sessions);
        binding.Register(account);
        var view = new AccountSettingsView(runtime, sessions);
        var window = new Window { Content = view, Width = 740, Height = 760 };
        try
        {
            window.Show();
            window.Close();
            validation.Complete(new() { Status = AccountValidationStatus.Valid, Uid = 42, UserName = "late name" });
            await view.Initialization;
            Assert.Equal("original", account.Name);
            Assert.False(File.Exists(Path.Combine(directory, "accounts.json")));
        }
        finally
        {
            validation.Complete(new() { Status = AccountValidationStatus.NotBound });
            window.Close();
            await view.Initialization;
            await runtime.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }
}
