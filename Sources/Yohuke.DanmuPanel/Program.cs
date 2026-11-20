using Avalonia;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var exceptionHandler = new AppExceptionHandler();
        try
        {
            BuildAvaloniaApp()
                .AfterPlatformServicesSetup(_ => exceptionHandler.AttachDispatcher(Dispatcher.UIThread))
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            AppExceptionHandler.Report(nameof(Program), exception);
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}
