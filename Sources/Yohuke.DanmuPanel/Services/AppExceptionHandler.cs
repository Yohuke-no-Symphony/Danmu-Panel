using Avalonia;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Models.Logging;

namespace Yohuke.DanmuPanel.Services;

internal sealed class AppExceptionHandler : IDisposable
{
    private Dispatcher? dispatcher;

    internal AppExceptionHandler()
    {
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    internal void AttachDispatcher(Dispatcher uiDispatcher)
    {
        dispatcher = uiDispatcher;
        dispatcher.UnhandledException += OnDispatcherUnhandledException;
    }

    internal static void Report(string source, Exception exception)
    {
        try
        {
            var sensitiveValues = (Application.Current as App)?.GetLogSensitiveValues();
            StartupLog.Append(exception.Message, AppLogLevel.ERROR, source, exception, sensitiveValues);
        }
        catch (Exception loggingFailure)
        {
            StartupLog.WriteToStandardError($"{source}: {exception.GetType().FullName}{Environment.NewLine}" +
                $"Log reporting failed ({loggingFailure.GetType().FullName}).");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (dispatcher is not null)
        {
            dispatcher.UnhandledException -= OnDispatcherUnhandledException;
            dispatcher = null;
        }
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        Report("Avalonia.Dispatcher", args.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        args.SetObserved();
        Report(nameof(TaskScheduler), args.Exception);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            Report(nameof(AppDomain), exception);
        }
    }
}
