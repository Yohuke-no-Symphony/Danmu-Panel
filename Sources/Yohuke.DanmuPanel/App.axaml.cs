using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Updates;
using Yohuke.DanmuPanel.ViewModels.Updates;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views.Settings;
using Yohuke.DanmuPanel.Views.Updates;

namespace Yohuke.DanmuPanel;

/// <summary>组装阅读台及其持久化生命周期。</summary>
public partial class App : Application
{
    private ShellWindow? shell;
    private PanelRuntime? runtime;
    private WorkspaceView? workspace;
    private TestMessagesView? testMessages;
    private readonly AppLogService log = new();
    private AppLogRuntimeBridge? logBridge;
    private LogPanelView? logPanel;
    private readonly List<MessageFeedView> feeds = [];
    private bool settingsOpen;
    private HttpClient? updateHttp;
    private AppUpdateViewModel? updateModel;
    private AppUpdateWindowCoordinator? updates;

    /// <inheritdoc/>
    public override void Initialize()
    {
        Localization.Initialize(Path.Combine(AppContext.BaseDirectory, "i18n"));
        AvaloniaXamlLoader.Load(this);
    }

    private async void SelectRoom(RoomTabState? room)
    {
        try
        {
            await SelectRoomAsync(room);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private Task SelectRoomAsync(RoomTabState? room, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        testMessages?.SelectRoom(room);
        return Task.WhenAll(feeds.Select(feed => feed.Model.SelectRoomAsync(room, cancellationToken)));
    }

    private async Task NavigateToMessageAsync(MessageRecord record)
    {
        if (shell is null || workspace?.SelectedRoom?.Id != record.RoomTabId)
        {
            return;
        }
        var panelId = record.Kind switch
        {
            MessageKind.SuperChat => WorkbenchPanelIds.SUPER_CHAT,
            MessageKind.Gift => WorkbenchPanelIds.GIFT,
            MessageKind.RedPocket => WorkbenchPanelIds.RED_POCKET,
            MessageKind.Guard => WorkbenchPanelIds.GUARD,
            _ => null
        };
        if (panelId is null)
        {
            return;
        }
        shell.ShowPanel(panelId);
        await feeds.Single(feed => feed.Kind == record.Kind).PingAsync(record);
    }

    private async void RefreshLanguage(object? sender, EventArgs args)
    {
        try
        {
            await Task.WhenAll(feeds.Select(feed => feed.Model.RefreshAsync()));
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async void OpenSettings()
    {
        if (settingsOpen || runtime is null || shell is null)
        {
            return;
        }
        settingsOpen = true;
        try
        {
            var dialog = new SettingsWindow(runtime, new AccountSettingsView(runtime));
            dialog.WindowMenuOnMacChanged += value => shell.UseWindowMenuOnMac = value;
            dialog.ClearRequested += Clear;
            dialog.CheckForUpdatesRequested += () => CheckUpdatesManually(dialog);
            dialog.StartupUpdatePreferenceChanged += enabled => updateModel?.SetStartupCheckEnabled(enabled);
            await shell.ShowModalAsync<object?>(dialog);
            await Task.WhenAll(feeds.Select(feed => feed.Model.RefreshAsync()));
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
        finally
        {
            settingsOpen = false;
        }
    }

    private async void Clear(bool all)
    {
        if (runtime is null || shell is null || !all && workspace?.SelectedRoom is null)
        {
            return;
        }
        try
        {
            var room = all ? null : workspace!.SelectedRoom;
            var detail = all ? Localization.Get("Panel.ClearAllConfirm") : Localization.Format("Panel.ClearRoomConfirm", room!.Name);
            if (await shell.ShowModalAsync<bool>(new ConfirmationWindow(Localization.Get(all ? "Shell.ClearAll" : "Shell.ClearRoom"), detail)))
            {
                await runtime.ClearAsync(room);
                await SelectRoomAsync(workspace?.SelectedRoom);
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void ReportFailure(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StartupLog.Append(LocalizedExceptionText.Get(exception), Models.Logging.AppLogLevel.ERROR,
                Localization.Get("Panel.Log.Operation"), exception, GetLogSensitiveValues());
        });
    }

    internal string[] GetLogSensitiveValues()
    {
        return [runtime?.Configuration.Settings.Translation.ApiKey ?? string.Empty];
    }

    private async void CheckStartupUpdates(object? sender, EventArgs args)
    {
        try
        {
            if (runtime!.Configuration.Settings.CheckForUpdatesOnStartup)
            {
                var coordinator = await EnsureUpdatesAsync();
                await coordinator.CheckOnStartupAsync();
            }
        }
        catch (Exception exception)
        {
            ReportUpdateFailure(exception);
        }
    }

    private async void CheckUpdatesManually(Window owner)
    {
        try
        {
            var coordinator = await EnsureUpdatesAsync();
            await coordinator.CheckManuallyAsync(owner);
        }
        catch (Exception exception)
        {
            ReportUpdateFailure(exception);
        }
    }

    private async Task<AppUpdateWindowCoordinator> EnsureUpdatesAsync()
    {
        if (updates is { } existing)
        {
            return existing;
        }

        var owner = shell!;
        var http = new HttpClient(new HttpClientHandler { UseCookies = false });
        AppUpdateViewModel? model = null;
        try
        {
            model = new(new AppUpdateService(new GitHubAppUpdateReleaseSource(http)),
                runtime!.Configuration.Settings, AppUpdateVersion.Current,
                uri => (updates?.CurrentWindow?.Launcher ?? owner.Launcher).LaunchUriAsync(uri),
                ReportUpdateFailure);
            var coordinator = new AppUpdateWindowCoordinator(owner, model, () => owner.IsInteractionBlocked);
            owner.InteractionChanged += coordinator.RefreshPresentation;
            updateHttp = http;
            updateModel = model;
            updates = coordinator;
            return coordinator;
        }
        catch
        {
            http.Dispose();
            if (model is not null)
            {
                await model.DisposeAsync();
            }
            throw;
        }
    }

    private static void ReportUpdateFailure(Exception exception)
    {
        StartupLog.Append(Localization.Get("Updates.CheckFailed"), Models.Logging.AppLogLevel.WARNING,
            Localization.Get("Updates.Title"), exception);
    }

    private async void OnShellClosed(object? sender, EventArgs args)
    {
        try
        {
            if (updates is not null)
            {
                shell!.InteractionChanged -= updates.RefreshPresentation;
                await updates.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            StartupLog.Append(LocalizedExceptionText.Get(exception), Models.Logging.AppLogLevel.ERROR,
                Localization.Get("Updates.Title"), exception);
        }
        finally
        {
            updateHttp?.Dispose();
            DisposeLog();
        }
    }

    private void DisposeLog()
    {
        logPanel?.Dispose();
        logBridge?.Dispose();
        log.Dispose();
    }
}
