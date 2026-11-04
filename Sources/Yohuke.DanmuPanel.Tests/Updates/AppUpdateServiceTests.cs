using Yohuke.DanmuPanel.Models.Updates;
using Yohuke.DanmuPanel.Services.Updates;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Updates;

/// <summary>验证更新判断独立于 HTTP，并正确区分无发布、已有新版及本地已最新。</summary>
public sealed class AppUpdateServiceTests
{
    /// <summary>规范化缺省数字段后比较版本，不使用字符串排序。</summary>
    [Theory]
    [InlineData("0.0.10", "0.0.9", AppUpdateCheckStatus.UPDATE_AVAILABLE)]
    [InlineData("1.2.3.1", "1.2.3", AppUpdateCheckStatus.UPDATE_AVAILABLE)]
    [InlineData("2.0", "1.9.9", AppUpdateCheckStatus.UPDATE_AVAILABLE)]
    [InlineData("1.2.3", "1.2.3.0", AppUpdateCheckStatus.UP_TO_DATE)]
    [InlineData("1.2.0.0", "1.2", AppUpdateCheckStatus.UP_TO_DATE)]
    [InlineData("1.2", "1.2.0.0", AppUpdateCheckStatus.UP_TO_DATE)]
    [InlineData("0.0.9", "0.0.10", AppUpdateCheckStatus.UP_TO_DATE)]
    [InlineData("1.0.0", "2.0.0", AppUpdateCheckStatus.UP_TO_DATE)]
    public async Task NumericComparisonDeterminesUpdateStatus(string available, string current, AppUpdateCheckStatus expected)
    {
        var release = Release(available);
        var source = new AppUpdateReleaseSourceStub(_ => Task.FromResult<AppUpdateInfo?>(release));

        var result = await new AppUpdateService(source).CheckAsync(Version.Parse(current));

        Assert.Equal(expected, result.Status);
        Assert.Same(release, result.Release);
    }

    /// <summary>不存在正式发布不能被显示为已是最新。</summary>
    [Fact]
    public async Task MissingReleaseHasItsOwnStatus()
    {
        var source = new AppUpdateReleaseSourceStub(_ => Task.FromResult<AppUpdateInfo?>(null));

        var result = await new AppUpdateService(source).CheckAsync(new(1, 0, 0));

        Assert.Equal(AppUpdateCheckStatus.NO_RELEASE, result.Status);
        Assert.Null(result.Release);
    }

    /// <summary>发布源异常交给表现层分类，不能转换成无更新。</summary>
    [Fact]
    public async Task SourceFailureRemainsFailure()
    {
        var failure = new HttpRequestException("Network unavailable.");
        var source = new AppUpdateReleaseSourceStub(_ => Task.FromException<AppUpdateInfo?>(failure));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new AppUpdateService(source).CheckAsync(new(1, 0)));

        Assert.Same(failure, error);
    }

    /// <summary>即使发布源忽略取消并返回结果，也不能继续发布迟到结果。</summary>
    [Fact]
    public async Task CancellationRejectsALateRelease()
    {
        using var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<AppUpdateInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = CancellationToken.None;
        var source = new AppUpdateReleaseSourceStub(token =>
        {
            observedToken = token;
            return completion.Task;
        });
        var check = new AppUpdateService(source).CheckAsync(new(1, 0), cancellation.Token);
        Assert.Equal(cancellation.Token, observedToken);

        await cancellation.CancelAsync();
        completion.SetResult(Release("2.0"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
    }

    /// <summary>已取消的检查不会访问发布源。</summary>
    [Fact]
    public async Task AlreadyCancelledCheckDoesNotContactTheSource()
    {
        var calls = 0;
        var source = new AppUpdateReleaseSourceStub(_ =>
        {
            calls++;
            return Task.FromResult<AppUpdateInfo?>(null);
        });
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new AppUpdateService(source).CheckAsync(new(1, 0), cancellation.Token));
        Assert.Equal(0, calls);
    }

    private static AppUpdateInfo Release(string version)
    {
        return new(Version.Parse(version), "v" + version, "版本 " + version, "发布说明",
            new("https://github.com/Yohuke-no-Symphony/DanmuPanel/releases/tag/v" + version));
    }
}
