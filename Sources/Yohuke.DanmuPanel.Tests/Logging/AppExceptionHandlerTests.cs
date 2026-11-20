using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.Models.Logging;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Logging;

/// <summary>验证未处理的 UI 回调异常进入会话日志后，后续回调仍能运行。</summary>
public sealed class AppExceptionHandlerTests
{
    /// <summary>调度器捕获异常、记录堆栈，并继续处理队列。</summary>
    [AvaloniaFact]
    public void DispatcherFailureIsLoggedAndFollowingWorkRuns()
    {
        using var log = new AppLogService();
        using var bridge = new AppLogRuntimeBridge(log);
        using var handler = new AppExceptionHandler();
        handler.AttachDispatcher(Dispatcher.UIThread);

        const string secret = "dispatcher-test-token";
        var nextCallbackRan = false;
        Dispatcher.UIThread.Post(() => throw new InvalidOperationException($"UI callback failed with api_key={secret}"));
        Dispatcher.UIThread.Post(() => nextCallbackRan = true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(nextCallbackRan);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(AppLogLevel.ERROR, entry.Level);
        Assert.Equal("Avalonia.Dispatcher", entry.Source);
        Assert.Contains("UI callback failed", entry.Message);
        Assert.Contains(nameof(InvalidOperationException), entry.Details);
        Assert.Contains(nameof(DispatcherFailureIsLoggedAndFollowingWorkRuns), entry.Details);
        Assert.DoesNotContain(secret, entry.FullText);
    }
}
