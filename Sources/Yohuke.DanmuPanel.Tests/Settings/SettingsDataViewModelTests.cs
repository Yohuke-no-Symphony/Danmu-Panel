using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证缓存统计的异步状态、显示单位和失败恢复。</summary>
public sealed class SettingsDataViewModelTests
{
    /// <summary>统计过程中显示加载状态并禁止重复刷新，完成后通知绑定更新。</summary>
    [AvaloniaFact]
    public async Task RefreshShowsProgressAndUpdatesTheFormattedSize()
    {
        var completion = new TaskCompletionSource<long>();
        var model = new SettingsDataViewModel("data", _ => completion.Task, () => Task.CompletedTask);
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        var refresh = model.RefreshCacheSizeCommand.ExecuteAsync(null);

        Assert.Equal(Localization.Get("Settings.CacheSizeCalculating"), model.CacheSizeText);
        Assert.False(model.RefreshCacheSizeCommand.CanExecute(null));

        completion.SetResult(1536);
        await refresh;

        Assert.Equal(1536, model.CacheSizeBytes);
        Assert.Equal($"{1.5} KiB", model.CacheSizeText);
        Assert.Contains(nameof(SettingsDataViewModel.CacheSizeText), changed);
        Assert.True(model.RefreshCacheSizeCommand.CanExecute(null));
    }

    /// <summary>各数量级使用二进制单位，并保留零字节的明确显示。</summary>
    [AvaloniaTheory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KiB")]
    [InlineData(1048576L, "1 MiB")]
    [InlineData(5368709120L, "5 GiB")]
    public async Task FormatsCacheSizeWithoutIntegerOverflow(long size, string expected)
    {
        var model = new SettingsDataViewModel("data", _ => Task.FromResult(size), () => Task.CompletedTask);

        await model.RefreshCacheSizeCommand.ExecuteAsync(null);

        Assert.Equal(expected, model.CacheSizeText);
    }

    /// <summary>读取失败不会显示为零或保留旧总量，用户可再次刷新恢复。</summary>
    [AvaloniaFact]
    public async Task FailedScanShowsUnavailableAndCanBeRetried()
    {
        var attempts = 0;
        var model = new SettingsDataViewModel("data", _ => ++attempts == 1
            ? Task.FromException<long>(new UnauthorizedAccessException())
            : Task.FromResult(42L), () => Task.CompletedTask);

        await model.RefreshCacheSizeCommand.ExecuteAsync(null);

        Assert.Null(model.CacheSizeBytes);
        Assert.Equal(Localization.Get("Settings.CacheSizeUnavailable"), model.CacheSizeText);

        await model.RefreshCacheSizeCommand.ExecuteAsync(null);

        Assert.Equal("42 B", model.CacheSizeText);
    }

    /// <summary>主动取消保留上次统计值并恢复按钮，后续刷新仍可执行。</summary>
    [AvaloniaFact]
    public async Task CancelingRefreshRetainsThePreviousSizeAndAllowsAnotherRefresh()
    {
        var attempts = 0;
        var model = new SettingsDataViewModel("data", async cancellationToken =>
        {
            if (++attempts == 2)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return 7;
        }, () => Task.CompletedTask);
        await model.RefreshCacheSizeCommand.ExecuteAsync(null);

        var pending = model.RefreshCacheSizeCommand.ExecuteAsync(null);
        model.RefreshCacheSizeCommand.Cancel();
        await pending;

        Assert.Equal("7 B", model.CacheSizeText);
        Assert.True(model.RefreshCacheSizeCommand.CanExecute(null));
        await model.RefreshCacheSizeCommand.ExecuteAsync(null);
        Assert.Equal(3, attempts);
    }
}
