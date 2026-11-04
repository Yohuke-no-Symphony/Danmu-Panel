using Yohuke.DanmuPanel.Models.Logging;

namespace Yohuke.DanmuPanel.Services;

internal sealed class AppLogRuntimeBridge : IDisposable
{
    private readonly IAppLogService log;
    private readonly Func<IEnumerable<string>> getSensitiveValues;
    private readonly Lock gate = new();
    private bool disposed;

    internal AppLogRuntimeBridge(IAppLogService log, Func<IEnumerable<string>>? getSensitiveValues = null)
    {
        this.log = log;
        this.getSensitiveValues = getSensitiveValues ?? (() => []);
        StartupLog.Written += OnStartupLog;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            StartupLog.Written -= OnStartupLog;
        }
    }

    private void OnStartupLog(AppLogLevel level, string source, string message, Exception? error)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var sensitiveValues = getSensitiveValues().ToArray();
            var safeSource = AppLogSanitizer.Sanitize(source, sensitiveValues);
            var safeMessage = AppLogSanitizer.Sanitize(PanelErrorText.Render(message), sensitiveValues);
            var details = error is null ? null : AppLogSanitizer.Sanitize(error.ToString(), sensitiveValues);
            switch (level)
            {
                case AppLogLevel.ERROR:
                    log.Error(safeSource, safeMessage, details: details);
                    break;
                case AppLogLevel.WARNING:
                    log.Warning(safeSource, safeMessage, details);
                    break;
                case AppLogLevel.INFO:
                    log.Information(safeSource, safeMessage, details);
                    break;
            }
        }
    }
}
