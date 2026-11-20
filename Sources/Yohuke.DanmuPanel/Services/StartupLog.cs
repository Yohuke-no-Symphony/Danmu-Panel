using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Logging;

namespace Yohuke.DanmuPanel.Services;

/// <summary>
/// 记录应用启动过程。
/// </summary>
public static class StartupLog
{
    private static readonly object LOCKER = new();
    private static bool isInitialized;

    internal static event Action<AppLogLevel, string, string, Exception?>? Written;

    /// <summary>
    /// 追加启动日志。
    /// </summary>
    public static void Append(string message, AppLogLevel level = AppLogLevel.INFO,
        string? source = null, Exception? error = null, IEnumerable<string>? sensitiveValues = null)
    {
        var secrets = sensitiveValues?.ToArray();
        var safeMessage = DiagnosticSanitizer.Sanitize(message, secrets);
        WriteEntry(level, safeMessage, error, secrets);

        var sourceName = source ?? (Localization.IsInitialized ? Localization.Get("Services.Log.Startup") : "Startup");
        try
        {
            Written?.Invoke(level, sourceName, safeMessage, error);
        }
        catch (Exception exception)
        {
            WriteEntry(AppLogLevel.ERROR, "Failed to publish an application log entry.", exception, secrets);
        }
    }

    private static void WriteEntry(AppLogLevel level, string safeMessage, Exception? error, string[]? secrets)
    {
        var details = error is null ? "" : Environment.NewLine + DiagnosticSanitizer.Sanitize(error.ToString(), secrets);
        var entry = $"{DateTimeOffset.Now:O} {level} {safeMessage}{details}";
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Yohuke.DanmuPanel");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "startup.log");

            lock (LOCKER)
            {
                if (!isInitialized)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    isInitialized = true;
                }

                File.AppendAllText(path, entry + Environment.NewLine);
            }
        }
        catch (Exception exception)
        {
            WriteToStandardError(entry + Environment.NewLine + DiagnosticSanitizer.Sanitize(exception.ToString(), secrets));
        }
    }

    internal static void WriteToStandardError(string entry)
    {
        try
        {
            Console.Error.WriteLine(entry);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
