using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Views;

namespace Yohuke.DanmuPanel.Windowing;

internal sealed class StartupCoordinator
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly StartupWindow loadingWindow;
    private readonly Func<CancellationToken, Task<Window>> initialize;
    private readonly Func<Task> abandon;
    private readonly Func<Exception, Window> createFailureWindow;
    private readonly CancellationTokenSource startupCancellation = new();
    private Task? cancellationTask;

    internal Task? Completion { get; private set; }

    internal StartupCoordinator(IClassicDesktopStyleApplicationLifetime desktop, StartupWindow loadingWindow,
        Func<CancellationToken, Task<Window>> initialize, Func<Task> abandon, Func<Exception, Window> createFailureWindow)
    {
        this.desktop = desktop;
        this.loadingWindow = loadingWindow;
        this.initialize = initialize;
        this.abandon = abandon;
        this.createFailureWindow = createFailureWindow;
        desktop.MainWindow = loadingWindow;
        desktop.ShutdownRequested += OnShutdownRequested;
        loadingWindow.Opened += OnOpened;
        loadingWindow.Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs args)
    {
        loadingWindow.Opened -= OnOpened;
        Completion = RunAsync();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        args.Cancel = true;
        RequestCancellation();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs args)
    {
        args.Cancel = true;
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        if (cancellationTask is null)
        {
            loadingWindow.ShowStage(StartupStage.Cancelling);
            cancellationTask = startupCancellation.CancelAsync();
        }
    }

    private async Task RunAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        var cancellationToken = startupCancellation.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextWindow = await initialize(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ShowMainWindow(nextWindow);
        }
        catch (Exception failure)
        {
            if (cancellationTask is { } pendingCancellation)
            {
                await pendingCancellation.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext |
                    ConfigureAwaitOptions.SuppressThrowing);
            }
            try
            {
                await abandon();
            }
            catch (Exception cleanupFailure)
            {
                failure = new AggregateException(failure, cleanupFailure);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                ShowMainWindow(createFailureWindow(failure));
            }
        }
        finally
        {
            desktop.ShutdownRequested -= OnShutdownRequested;
            loadingWindow.Closing -= OnClosing;
            startupCancellation.Dispose();
            loadingWindow.Close();
        }
    }

    private void ShowMainWindow(Window window)
    {
        desktop.MainWindow = window;
        window.Show();
    }
}
