using System.Net;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Updates;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Updates;

/// <summary>验证启动与手动检查的共享请求、反馈、取消和资源释放。</summary>
public sealed class AppUpdateViewModelTests
{
    /// <summary>构造不访问网络；启动检查只执行一次且不会重复已关闭的提示。</summary>
    [AvaloniaFact]
    public async Task StartupChecksOnceAndDoesNotRepeatDismissedResult()
    {
        await using var context = new AppUpdateTestContext();
        Assert.Equal(0, context.Service.CallCount);
        var first = context.Model.CheckOnStartupAsync();
        var second = context.Model.CheckOnStartupAsync();
        Assert.Same(first, second);
        Assert.False(context.Model.IsDialogOpen);
        Assert.Equal(new Version(1, 0, 0), context.Service.CurrentVersion);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await first;

        Assert.True(context.Model.IsDialogOpen);
        Assert.True(context.Model.HasUpdate);
        context.Model.Dismiss();
        await context.Model.CheckOnStartupAsync();
        Assert.False(context.Model.IsDialogOpen);
        Assert.Equal(1, context.Service.CallCount);
    }

    /// <summary>禁用启动检查不会发送请求，但手动检查仍可工作并立即显示检查中。</summary>
    [AvaloniaFact]
    public async Task DisabledStartupStillAllowsManualChecks()
    {
        await using var context = new AppUpdateTestContext(new() { CheckForUpdatesOnStartup = false });
        await context.Model.CheckOnStartupAsync();
        Assert.Equal(0, context.Service.CallCount);
        var check = context.Model.CheckManuallyAsync();
        Assert.True(context.Model.IsChecking);
        Assert.True(context.Model.IsDialogOpen);
        Assert.Equal(Localization.Get("Updates.Checking"), context.Model.Message);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
        await check;
        Assert.False(context.Model.IsChecking);
        Assert.Equal(Localization.Get("Updates.NoRelease"), context.Model.Message);
    }

    /// <summary>自动无更新、无发布和失败均不显示提示；手动检查显示最终反馈。</summary>
    [AvaloniaTheory]
    [InlineData(false, AppUpdateCheckStatus.NO_RELEASE)]
    [InlineData(false, AppUpdateCheckStatus.UP_TO_DATE)]
    [InlineData(true, AppUpdateCheckStatus.NO_RELEASE)]
    [InlineData(true, AppUpdateCheckStatus.UP_TO_DATE)]
    public async Task ResultVisibilityFollowsTheTrigger(bool manual, AppUpdateCheckStatus status)
    {
        await using var context = new AppUpdateTestContext();
        context.Service.Response.SetResult(new(status, null));
        await (manual ? context.Model.CheckManuallyAsync() : context.Model.CheckOnStartupAsync());

        Assert.Equal(manual, context.Model.IsDialogOpen);
        Assert.False(context.Model.HasUpdate);
        Assert.Equal(Localization.Get(status == AppUpdateCheckStatus.NO_RELEASE
            ? "Updates.NoRelease" : "Updates.UpToDate"), context.Model.Message);
    }

    /// <summary>手动加入自动检查只发一次请求，关闭自动开关不取消手动意图。</summary>
    [AvaloniaFact]
    public async Task ManualIntentJoinsAnAutomaticRequest()
    {
        await using var context = new AppUpdateTestContext();
        var automatic = context.Model.CheckOnStartupAsync();
        var manual = context.Model.CheckManuallyAsync();
        Assert.Same(automatic, manual);
        Assert.Same(manual, context.Model.CheckManuallyAsync());
        context.Model.SetStartupCheckEnabled(false);
        Assert.False(context.Service.CancellationToken.IsCancellationRequested);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
        await manual;

        Assert.True(context.Model.IsDialogOpen);
        Assert.Equal(1, context.Service.CallCount);
    }

    /// <summary>关闭自动开关会取消本次自动检查，重新开启也不接受旧响应。</summary>
    [AvaloniaFact]
    public async Task DisablingAutomaticCheckRejectsItsLateResponse()
    {
        await using var context = new AppUpdateTestContext();
        var check = context.Model.CheckOnStartupAsync();
        context.Model.SetStartupCheckEnabled(false);
        Assert.True(context.Service.CancellationToken.IsCancellationRequested);
        context.Model.SetStartupCheckEnabled(true);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await check;

        Assert.False(context.Model.IsDialogOpen);
        Assert.False(context.Model.HasUpdate);
    }

    /// <summary>手动检查窗口关闭后会取消请求并拒绝迟到结果。</summary>
    [AvaloniaFact]
    public async Task DismissingManualCheckRejectsItsLateResponse()
    {
        await using var context = new AppUpdateTestContext();
        var check = context.Model.CheckManuallyAsync();
        context.Model.Dismiss();
        Assert.True(context.Service.CancellationToken.IsCancellationRequested);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await check;

        Assert.False(context.Model.IsDialogOpen);
        Assert.False(context.Model.HasUpdate);
        Assert.False(context.Model.IsChecking);
    }

    /// <summary>旧请求取消后的迟到响应不能覆盖新的手动检查。</summary>
    [AvaloniaFact]
    public async Task ReopenedCheckIsNotOverwrittenByAnOlderRequest()
    {
        await using var context = new AppUpdateTestContext();
        var firstResponse = context.Service.Response;
        var first = context.Model.CheckManuallyAsync();
        context.Model.Dismiss();
        context.Service.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = context.Model.CheckManuallyAsync();
        firstResponse.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await first;
        Assert.True(context.Model.IsChecking);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
        await second;

        Assert.False(context.Model.HasUpdate);
        Assert.Equal(Localization.Get("Updates.NoRelease"), context.Model.Message);
    }

    /// <summary>手动错误按限流、超时和非法数据分类，自动错误只交给日志。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureFeedbackCanRetry(bool manual)
    {
        await using var context = new AppUpdateTestContext();
        var failure = new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests);
        context.Service.Response.SetException(failure);
        await (manual ? context.Model.CheckManuallyAsync() : context.Model.CheckOnStartupAsync());

        Assert.Equal(manual, context.Model.IsDialogOpen);
        Assert.Equal(Localization.Get("Updates.RateLimited"), context.Model.Message);
        Assert.Same(failure, context.Failure);
        Assert.True(context.Model.CanRetry);
        context.Service.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = context.Model.RetryCommand.ExecuteAsync(null);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
        await retry;
        Assert.True(context.Model.IsDialogOpen);
        Assert.False(context.Model.CanRetry);
    }

    /// <summary>分类错误文案不会混淆超时、非法数据与网络失败。</summary>
    [AvaloniaFact]
    public async Task FailuresUseSpecificMessages()
    {
        foreach (var (error, key) in new (Exception, string)[]
        {
            (new TimeoutException(), "Updates.TimedOut"),
            (new InvalidDataException(), "Updates.InvalidRelease"),
            (new HttpRequestException("offline"), "Updates.CheckFailed"),
            (new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden), "Updates.RateLimited")
        })
        {
            await using var context = new AppUpdateTestContext();
            context.Service.Response.SetException(error);
            await context.Model.CheckManuallyAsync();
            Assert.Equal(Localization.Get(key), context.Model.Message);
        }
    }

    /// <summary>停止并等待检查后仍可重新手动检查，适用于关闭保存失败后的重试。</summary>
    [AvaloniaFact]
    public async Task CancellingPendingChecksDoesNotDisposeTheFeature()
    {
        await using var context = new AppUpdateTestContext();
        var check = context.Model.CheckOnStartupAsync();
        var stopped = context.Model.CancelPendingAsync();
        Assert.False(stopped.IsCompleted);
        Assert.True(context.Service.CancellationToken.IsCancellationRequested);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await stopped;
        await check;
        context.Service.Response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var manual = context.Model.CheckManuallyAsync();
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
        await manual;

        Assert.True(context.Model.IsDialogOpen);
        Assert.Equal(2, context.Service.CallCount);
    }

    /// <summary>释放会等待已取消请求结束，并拒绝新的检查和迟到结果。</summary>
    [AvaloniaFact]
    public async Task DisposalAwaitsRequestsAndPreventsFurtherChecks()
    {
        await using var context = new AppUpdateTestContext();
        var check = context.Model.CheckManuallyAsync();
        var disposal = context.Model.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.True(context.Service.CancellationToken.IsCancellationRequested);
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await disposal;
        await check;
        await context.Model.CheckManuallyAsync();

        Assert.False(context.Model.IsDialogOpen);
        Assert.False(context.Model.HasUpdate);
        Assert.Equal(1, context.Service.CallCount);
    }

    /// <summary>打开对应 Release 失败后保留更新信息，允许重试打开浏览器。</summary>
    [AvaloniaFact]
    public async Task BrowserFailureDoesNotDismissTheUpdate()
    {
        Uri? launched = null;
        var succeeds = false;
        await using var context = new AppUpdateTestContext(launchUri: uri =>
        {
            launched = uri;
            return Task.FromResult(succeeds);
        });
        context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
        await context.Model.CheckManuallyAsync();
        await context.Model.OpenReleaseCommand.ExecuteAsync(null);
        Assert.Equal(AppUpdateTestContext.Update.ReleaseUri, launched);
        Assert.True(context.Model.HasBrowserError);
        Assert.True(context.Model.IsDialogOpen);
        succeeds = true;
        await context.Model.OpenReleaseCommand.ExecuteAsync(null);
        Assert.False(context.Model.HasBrowserError);
    }

    /// <summary>切换语言后更新检查状态文案同步变化。</summary>
    [AvaloniaFact]
    public async Task StatusTracksLanguageChanges()
    {
        var original = Localization.SelectedLanguageID;
        await using var context = new AppUpdateTestContext();
        try
        {
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
            await context.Model.CheckManuallyAsync();
            Localization.SetLanguage("ja-JP");
            Assert.Equal(Localization.Get("Updates.NoRelease"), context.Model.Message);
            Assert.NotEqual("Updates.NoRelease", context.Model.Message);
        }
        finally
        {
            Localization.SetLanguage(original);
        }
    }
}
