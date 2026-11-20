using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Logging;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel;

public partial class App
{
    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                StartDesktop(desktop, static () => new());
            }
            catch (Exception exception)
            {
                desktop.MainWindow = CreateStartupFailureWindow(exception);
                DisposeLog();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    internal StartupCoordinator StartDesktop(IClassicDesktopStyleApplicationLifetime desktop,
        Func<ConfigurationService> createConfigurationStore)
    {
        Dispatcher.UIThread.VerifyAccess();
        logBridge = new(log, () => [runtime?.Configuration.Settings.Translation.ApiKey ?? string.Empty]);
        var loadingWindow = new StartupWindow();
        return new(desktop, loadingWindow, token => InitializeApplicationAsync(loadingWindow, createConfigurationStore, token),
            AbandonStartupAsync, CreateStartupFailureWindow);
    }

    private async Task<Window> InitializeApplicationAsync(StartupWindow loadingWindow,
        Func<ConfigurationService> createConfigurationStore, CancellationToken cancellationToken)
    {
        var (configStore, configuration) = await Task.Run(() =>
        {
            var store = createConfigurationStore();
            return (store, store.Load());
        }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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

        loadingWindow.ShowStage(StartupStage.TranslationCache);
        var panelRuntime = new PanelRuntime(configStore, configuration);
        runtime = panelRuntime;
        panelRuntime.Colors.Apply(this, configuration.Settings.ColorScheme);
        await panelRuntime.RestoreTranslationCacheAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        loadingWindow.ShowStage(StartupStage.Messages);
        await panelRuntime.InitializeMessagesAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        loadingWindow.ShowStage(StartupStage.Accounts);
        await panelRuntime.ValidateAccountsAsync(cancellationToken);

        loadingWindow.ShowStage(StartupStage.Layout);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        cancellationToken.ThrowIfCancellationRequested();
        var readyWindow = CreateShell(panelRuntime);
        await readyWindow.InitializeLayoutAsync(cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        loadingWindow.ShowStage(StartupStage.Room);
        await SelectRoomAsync(workspace!.SelectedRoom, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ConnectShell(readyWindow);
        return readyWindow;
    }

    private ShellWindow CreateShell(PanelRuntime panelRuntime)
    {
        workspace = new(panelRuntime, ReportFailure);
        var panels = new Dictionary<string, Control> { ["workspace"] = workspace };
        foreach (var (id, kind) in new[]
        {
            (WorkbenchPanelIds.DANMU, MessageKind.Danmu),
            (WorkbenchPanelIds.SUPER_CHAT, MessageKind.SuperChat),
            (WorkbenchPanelIds.GIFT, MessageKind.Gift),
            (WorkbenchPanelIds.GUARD, MessageKind.Guard),
            (WorkbenchPanelIds.RED_POCKET, MessageKind.RedPocket)
        })
        {
            var view = new MessageFeedView(panelRuntime, kind, ReportFailure, NavigateToMessageAsync);
            feeds.Add(view);
            panels.Add(id, view);
        }
        testMessages = new(panelRuntime, ReportFailure);
        panels.Add("testMessages", testMessages);
        logPanel = new(log);
        panels.Add("log", logPanel);
        var window = new ShellWindow(panels, workspace.Toolbar, panelRuntime.ConfigurationStore.DirectoryPath)
        {
            UseWindowMenuOnMac = panelRuntime.Configuration.Settings.UseWindowMenuOnMac
        };
        shell = window;
        workspace.HostWindow = window;
        return window;
    }

    private void ConnectShell(ShellWindow window)
    {
        NativeMenu.GetMenu(this)!.Items.OfType<NativeMenuItem>().First().Command = window.AboutCommand;
        workspace!.SelectedRoomChanged += SelectRoom;
        Localization.LanguageChanged += RefreshLanguage;
        window.SettingsRequested += OpenSettings;
        window.ClearRoomRequested += () => Clear(false);
        window.ClearAllRequested += () => Clear(true);
        window.OperationFailed += ReportFailure;
        window.Closed += OnShellClosed;
        window.BeforeCloseAsync = CloseSessionAsync;
        window.Opened += CheckStartupUpdates;
    }

    private async Task CloseSessionAsync()
    {
        if (updateModel is not null)
        {
            await updateModel.CancelPendingAsync();
        }
        await testMessages!.FinishPendingAsync();
        await runtime!.DisposeAsync();
        DisposeSessionViews();
    }

    private async Task AbandonStartupAsync()
    {
        try
        {
            if (shell is { } incompleteShell)
            {
                incompleteShell.BeforeCloseAsync = null;
                incompleteShell.Closed -= OnShellClosed;
                incompleteShell.Opened -= CheckStartupUpdates;
                NativeMenu.GetMenu(this)!.Items.OfType<NativeMenuItem>().First().Command = null;
                try
                {
                    await incompleteShell.CloseAsync();
                }
                catch (Exception cleanupFailure)
                {
                    LogStartupFailure(cleanupFailure);
                }
            }
        }
        finally
        {
            try
            {
                DisposeSessionViews();
            }
            finally
            {
                try
                {
                    if (runtime is { } incompleteRuntime)
                    {
                        await incompleteRuntime.AbandonStartupAsync();
                    }
                }
                catch (Exception cleanupFailure)
                {
                    LogStartupFailure(cleanupFailure);
                }
                finally
                {
                    DisposeLog();
                }
            }
        }
    }

    private void DisposeSessionViews()
    {
        Localization.LanguageChanged -= RefreshLanguage;
        foreach (var feed in feeds)
        {
            feed.Model.Dispose();
        }
        if (workspace is not null)
        {
            workspace.SelectedRoomChanged -= SelectRoom;
            workspace.Dispose();
        }
        testMessages?.Dispose();
    }

    private Window CreateStartupFailureWindow(Exception exception)
    {
        LogStartupFailure(exception);
        return new Window
        {
            Title = Localization.Get("Panel.StartupFailed"),
            Width = 650,
            Height = 300,
            Content = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Text = DiagnosticSanitizer.Sanitize(exception.ToString(), [runtime?.Configuration.Settings.Translation.ApiKey ?? ""]),
                Margin = new Thickness(20)
            }
        };
    }

    private void LogStartupFailure(Exception exception)
    {
        StartupLog.Append(LocalizedExceptionText.Get(exception), AppLogLevel.ERROR, error: exception,
            sensitiveValues: [runtime?.Configuration.Settings.Translation.ApiKey ?? ""]);
    }
}
