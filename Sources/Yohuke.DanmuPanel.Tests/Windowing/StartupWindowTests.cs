using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Windowing;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证启动期间界面响应、主窗口交接及资源清理。</summary>
public sealed class StartupWindowTests
{
    private static readonly TimeSpan TEST_TIMEOUT = TimeSpan.FromSeconds(5);

    /// <summary>应用的实际启动流程在 UI 线程创建运行时资源，并打开主窗口。</summary>
    [AvaloniaFact]
    public async Task ApplicationStartupCreatesAndShowsTheRealShell()
    {
        using var directory = new MessageStoreTestDirectory();
        using var desktop = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var store = new ConfigurationService(directory.Path);
        await store.SaveAsync(new() { Settings = new() { CheckForUpdatesOnStartup = false } });
        var language = Localization.SelectedLanguageID;
        var app = new App();
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem());
        NativeMenu.SetMenu(app, menu);
        var coordinator = app.StartDesktop(desktop, () => store);
        var loading = Assert.IsType<StartupWindow>(desktop.MainWindow);

        try
        {
            loading.Show();
            await coordinator.Completion!.WaitAsync(TEST_TIMEOUT);

            Assert.False(loading.IsVisible);
            var shell = Assert.IsType<ShellWindow>(desktop.MainWindow);
            Assert.True(shell.IsVisible);
        }
        finally
        {
            await CloseStartupAsync(loading, coordinator);
            if (desktop.MainWindow is ShellWindow shell)
            {
                await shell.CloseAsync();
            }
            else if (!ReferenceEquals(desktop.MainWindow, loading))
            {
                desktop.MainWindow?.Close();
            }
            Localization.SetLanguage(language);
        }
    }

    /// <summary>等待缓存恢复时展示真实阶段并处理输入，主窗口显示后再关闭启动窗。</summary>
    [AvaloniaFact]
    public async Task PendingStartupShowsTheCurrentStageAndHandsOffToTheShell()
    {
        using var desktop = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var loading = new StartupWindow();
        var shell = new Window();
        var cacheStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cacheReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = new List<string>();
        var loadingVisibleWhenShellOpened = false;
        Window? mainWhenShellOpened = null;
        shell.Opened += (_, _) =>
        {
            loadingVisibleWhenShellOpened = loading.IsVisible;
            mainWhenShellOpened = desktop.MainWindow;
            transitions.Add("shell");
        };
        loading.Closed += (_, _) => transitions.Add("closed loading");
        var abandoned = 0;
        var coordinator = new StartupCoordinator(desktop, loading, async cancellationToken =>
        {
            loading.ShowStage(StartupStage.TranslationCache);
            cacheStarted.SetResult();
            await cacheReady.Task.WaitAsync(cancellationToken);
            loading.ShowStage(StartupStage.Layout);
            return shell;
        }, () =>
        {
            abandoned++;
            return Task.CompletedTask;
        }, exception => throw new InvalidOperationException("Expected startup to succeed.", exception));

        try
        {
            Assert.Same(loading, desktop.MainWindow);
            loading.Show();
            await cacheStarted.Task.WaitAsync(TEST_TIMEOUT);

            var inputHandled = false;
            await Dispatcher.UIThread.InvokeAsync(() => inputHandled = true, DispatcherPriority.Input);
            Assert.True(inputHandled);
            Assert.True(loading.IsVisible);
            Assert.False(shell.IsVisible);
            Assert.Equal(StartupStage.TranslationCache, loading.Model.Stage);
            Assert.Equal(Localization.Get("Panel.Startup.TranslationCache"),
                Find<TextBlock>(loading, "StartupStatusText").Text);
            Assert.True(Find<ProgressBar>(loading, "StartupProgressBar").IsIndeterminate);

            cacheReady.SetResult();
            await coordinator.Completion!.WaitAsync(TEST_TIMEOUT);

            Assert.Equal(StartupStage.Layout, loading.Model.Stage);
            Assert.True(loadingVisibleWhenShellOpened);
            Assert.Same(shell, mainWhenShellOpened);
            Assert.Same(shell, desktop.MainWindow);
            Assert.True(shell.IsVisible);
            Assert.False(loading.IsVisible);
            Assert.Equal(0, abandoned);
            Assert.Equal(new[] { "shell", "closed loading" }, transitions);
        }
        finally
        {
            cacheReady.TrySetResult();
            await CloseStartupAsync(loading, coordinator);
            shell.Close();
        }
    }

    /// <summary>取消等待旧工作及清理完成，之后关闭且无后到窗口。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelFromTheWindowOrDesktopWaitsForCleanup(bool requestDesktopShutdown)
    {
        using var desktop = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var loading = new StartupWindow();
        var shell = new Window();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCount = 0;
        var coordinator = new StartupCoordinator(desktop, loading, async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await finishInitialization.Task;
            return shell;
        }, async () =>
        {
            cleanupCount++;
            cleanupStarted.TrySetResult();
            await finishCleanup.Task;
        }, exception => throw new InvalidOperationException("Expected cancellation rather than startup failure.", exception));

        try
        {
            loading.Show();
            await started.Task.WaitAsync(TEST_TIMEOUT);
            var cancel = Find<Button>(loading, "CancelStartupButton");
            Assert.True(cancel.IsEnabled);
            if (requestDesktopShutdown)
            {
                var coordinatorCancelledShutdown = false;
                desktop.ShutdownRequested += (_, request) =>
                {
                    coordinatorCancelledShutdown = request.Cancel;
                    request.Cancel = true;
                };
                Assert.False(desktop.TryShutdown());
                Assert.True(coordinatorCancelledShutdown);
            }
            else
            {
                cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }

            await cancelled.Task.WaitAsync(TEST_TIMEOUT);
            Assert.True(loading.IsVisible);
            Assert.Equal(StartupStage.Cancelling, loading.Model.Stage);
            Assert.False(cancel.IsEnabled);
            Assert.False(cleanupStarted.Task.IsCompleted);

            finishInitialization.SetResult();
            await cleanupStarted.Task.WaitAsync(TEST_TIMEOUT);
            Assert.True(loading.IsVisible);
            Assert.False(shell.IsVisible);
            finishCleanup.SetResult();
            await coordinator.Completion!.WaitAsync(TEST_TIMEOUT);

            Assert.False(loading.IsVisible);
            Assert.False(shell.IsVisible);
            Assert.NotSame(shell, desktop.MainWindow);
            Assert.Equal(1, cleanupCount);
        }
        finally
        {
            finishInitialization.TrySetResult();
            finishCleanup.TrySetResult();
            await CloseStartupAsync(loading, coordinator);
            shell.Close();
        }
    }

    /// <summary>启动失败清理已创建资源并把可见窗口交给错误页。</summary>
    [AvaloniaFact]
    public async Task StartupFailureCleansUpAndShowsTheFailureWindow()
    {
        using var desktop = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var loading = new StartupWindow();
        var failureWindow = new Window();
        var error = new InvalidOperationException("startup failed");
        Exception? shown = null;
        var transitions = new List<string>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failureWindow.Opened += (_, _) =>
        {
            transitions.Add("failure shown");
            completed.TrySetResult();
        };
        loading.Closed += (_, _) => transitions.Add("closed loading");
        var coordinator = new StartupCoordinator(desktop, loading, _ => Task.FromException<Window>(error), () =>
        {
            transitions.Add("cleaned up");
            return Task.CompletedTask;
        }, exception =>
        {
            shown = exception;
            return failureWindow;
        });

        try
        {
            loading.Show();
            await completed.Task.WaitAsync(TEST_TIMEOUT);
            await coordinator.Completion!.WaitAsync(TEST_TIMEOUT);

            Assert.Same(error, shown);
            Assert.Same(failureWindow, desktop.MainWindow);
            Assert.True(failureWindow.IsVisible);
            Assert.False(loading.IsVisible);
            Assert.Equal(new[] { "cleaned up", "failure shown", "closed loading" }, transitions);
        }
        finally
        {
            await CloseStartupAsync(loading, coordinator);
            failureWindow.Close();
        }
    }

    private static T Find<T>(StartupWindow window, string name) where T : Control
    {
        return Assert.IsType<T>(window.FindControl<T>(name));
    }

    private static async Task CloseStartupAsync(StartupWindow loading, StartupCoordinator coordinator)
    {
        if (loading.IsVisible)
        {
            loading.Close();
        }
        if (coordinator.Completion is { IsCompleted: false } completion)
        {
            await completion.WaitAsync(TEST_TIMEOUT);
        }
    }
}
