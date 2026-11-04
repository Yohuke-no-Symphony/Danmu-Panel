using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
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

    /// <inheritdoc/>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            logBridge = new(log,
                () => [runtime?.Configuration.Settings.Translation.ApiKey ?? string.Empty]);
            try
            {
                var configStore = new ConfigurationService();
                var configuration = configStore.Load();
                Localization.SetLanguage(configuration.Settings.Language);
                RequestedThemeVariant = configuration.Settings.Theme switch
                {
                    "light" => ThemeVariant.Light,
                    "dark" => ThemeVariant.Dark,
                    _ => ThemeVariant.Default
                };
                if (configuration.Workspaces.Count == 0)
                {
                    configuration.Workspaces.Add(new() { Name = Localization.Get("Panel.DefaultWorkspace") });
                }
                runtime = new(configStore, configuration);
                runtime.Colors.Apply(this, configuration.Settings.ColorScheme);
                workspace = new(runtime, ReportFailure);
                var panels = new Dictionary<string, Control> { ["workspace"] = workspace };
                foreach (var (id, kind) in new[] { ("danmu", MessageKind.Danmu), ("superChat", MessageKind.SuperChat), ("gift", MessageKind.Gift), ("guard", MessageKind.Guard) })
                {
                    var view = new MessageFeedView(runtime, kind, ReportFailure);
                    feeds.Add(view);
                    panels.Add(id, view);
                }
                testMessages = new(runtime, ReportFailure);
                panels.Add("testMessages", testMessages);
                logPanel = new(log);
                panels.Add("log", logPanel);
                shell = new(panels, workspace.Toolbar, configStore.DirectoryPath)
                {
                    UseWindowMenuOnMac = configuration.Settings.UseWindowMenuOnMac
                };
                NativeMenu.GetMenu(this)!.Items.OfType<NativeMenuItem>().First().Command = shell.AboutCommand;
                updateHttp = new(new HttpClientHandler { UseCookies = false });
                updateModel = new(new AppUpdateService(new GitHubAppUpdateReleaseSource(updateHttp)),
                    configuration.Settings, AppUpdateVersion.Current,
                    uri => (updates?.CurrentWindow?.Launcher ?? shell.Launcher).LaunchUriAsync(uri),
                    exception => StartupLog.Append(Localization.Get("Updates.CheckFailed"),
                        Models.Logging.AppLogLevel.WARNING, Localization.Get("Updates.Title"), exception));
                updates = new(shell, updateModel, () => shell.IsInteractionBlocked);
                shell.InteractionChanged += updates.RefreshPresentation;
                workspace.HostWindow = shell;
                workspace.SelectedRoomChanged += SelectRoom;
                Localization.LanguageChanged += RefreshLanguage;
                shell.SettingsRequested += OpenSettings;
                shell.ClearRoomRequested += () => Clear(false);
                shell.ClearAllRequested += () => Clear(true);
                shell.OperationFailed += ReportFailure;
                shell.Closed += OnShellClosed;
                shell.BeforeCloseAsync = async () =>
                {
                    await updateModel.CancelPendingAsync();
                    await testMessages.FinishPendingAsync();
                    await runtime.DisposeAsync();
                    foreach (var feed in feeds)
                    {
                        feed.Model.Dispose();
                    }
                    workspace.Dispose();
                    testMessages.Dispose();
                    Localization.LanguageChanged -= RefreshLanguage;
                };
                shell.Opened += CheckStartupUpdates;
                shell.Opened += async (_, _) =>
                {
                    try
                    {
                        await runtime.InitializeAsync();
                        await SelectRoomAsync(workspace.SelectedRoom);
                    }
                    catch (Exception exception)
                    {
                        ReportFailure(exception);
                    }
                };
                desktop.MainWindow = shell;
            }
            catch (Exception exception)
            {
                StartupLog.Append(exception.Message, Models.Logging.AppLogLevel.ERROR, error: exception);
                if (updates is not null)
                {
                    _ = updates.DisposeAsync();
                }
                updateHttp?.Dispose();
                DisposeLog();
                desktop.MainWindow = new Window
                {
                    Title = Localization.Get("Panel.StartupFailed"), Width = 650, Height = 300,
                    Content = new TextBox { IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = AppLogSanitizer.Sanitize(exception.ToString()), Margin = new Thickness(20) }
                };
            }
        }
        base.OnFrameworkInitializationCompleted();
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

    private Task SelectRoomAsync(RoomTabState? room)
    {
        testMessages?.SelectRoom(room);
        return Task.WhenAll(feeds.Select(feed => feed.Model.SelectRoomAsync(room)));
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
            dialog.StartupUpdatePreferenceChanged += updateModel!.SetStartupCheckEnabled;
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
            var key = runtime?.Configuration.Settings.Translation.ApiKey;
            StartupLog.Append(exception.Message, Models.Logging.AppLogLevel.ERROR,
                Localization.Get("Panel.Log.Operation"), exception, [key ?? string.Empty]);
        });
    }

    private async void CheckStartupUpdates(object? sender, EventArgs args)
    {
        try
        {
            await updates!.CheckOnStartupAsync();
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private async void CheckUpdatesManually(Window owner)
    {
        try
        {
            await updates!.CheckManuallyAsync(owner);
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
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
            StartupLog.Append(exception.Message, Models.Logging.AppLogLevel.ERROR,
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
