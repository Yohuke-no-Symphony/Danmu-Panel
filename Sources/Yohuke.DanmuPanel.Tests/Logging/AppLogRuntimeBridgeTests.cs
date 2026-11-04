using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Logging;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Logging;

/// <summary>验证既有后台日志进入会话日志，保留本地化与脱敏边界。</summary>
public sealed class AppLogRuntimeBridgeTests
{
    /// <summary>日志页未打开时仍接收翻译错误，并显示可读的错误信息。</summary>
    [AvaloniaFact]
    public void HiddenPanelRetainsLocalizedTranslationErrors()
    {
        using var log = new AppLogService();
        using var bridge = new AppLogRuntimeBridge(log);
        using var model = new LogPanelViewModel(log);

        StartupLog.Append("translation.empty_response", AppLogLevel.ERROR, "translation");
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(model.Entries);
        Assert.Equal(AppLogLevel.ERROR, entry.Level);
        Assert.Equal("translation", entry.Source);
        Assert.Equal(Localization.Get("translation.empty_response"), entry.Message);
        Assert.Equal(1, log.UnreadErrorCount);
    }

    /// <summary>完整异常详情与来源会脱敏，释放桥接后不再接收全局事件。</summary>
    [AvaloniaFact]
    public void BridgeRedactsDetailsAndStopsAfterDispose()
    {
        using var log = new AppLogService();
        var secret = "log-test-secret-9287";
        using var bridge = new AppLogRuntimeBridge(log, () => [secret]);
        var error = new InvalidOperationException("outer " + secret, new IOException("inner diagnostic"));

        StartupLog.Append(error.Message, AppLogLevel.ERROR, "source " + secret, error, [secret]);

        var entry = Assert.Single(log.Entries);
        Assert.DoesNotContain(secret, entry.FullText);
        Assert.Contains("inner diagnostic", entry.Details);
        Assert.Contains(nameof(IOException), entry.Details);
        bridge.Dispose();
        StartupLog.Append("later message");
        Assert.Single(log.Entries);
    }

    /// <summary>普通状态与警告保留原始严重程度，避免全部显示为错误。</summary>
    [AvaloniaFact]
    public void BridgePreservesInformationAndWarningLevels()
    {
        using var log = new AppLogService();
        using var bridge = new AppLogRuntimeBridge(log);

        StartupLog.Append("saved", source: "settings");
        StartupLog.Append("retrying", AppLogLevel.WARNING, "reception");

        Assert.Equal(new[] { AppLogLevel.INFO, AppLogLevel.WARNING }, log.Entries.Select(entry => entry.Level));
        Assert.Equal(0, log.UnreadErrorCount);
    }
}
