using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Updates;
using Yohuke.DanmuPanel.Views.Updates;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Updates;

/// <summary>验证更新窗口的唯一性、所属窗口、模态交互和清理。</summary>
public sealed class AppUpdateWindowTests
{
    /// <summary>手动检查归属设置窗口，检查中重复点击复用请求与窗口。</summary>
    [AvaloniaFact]
    public async Task ManualWindowUsesTheSettingsOwnerAndIsReused()
    {
        await using var context = new AppUpdateTestContext();
        var main = new Window();
        var settings = new Window();
        await using var coordinator = new AppUpdateWindowCoordinator(main, context.Model, () => true);
        try
        {
            main.Show();
            var settingsTask = settings.ShowDialog(main);
            var check = coordinator.CheckManuallyAsync(settings);
            var first = Assert.IsType<AppUpdateWindow>(coordinator.CurrentWindow);
            Assert.Same(settings, first.Owner);
            Assert.True(first.IsEnabled);
            Assert.Same(check, coordinator.CheckManuallyAsync(settings));
            Assert.Same(first, coordinator.CurrentWindow);
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await check;
            Pump(first);
            Assert.True(first.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenReleaseButton").IsVisible);
            Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "1.0.0");
            Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("v2.0.0") == true);
            await coordinator.CheckManuallyAsync(settings);
            Assert.Equal(1, context.Service.CallCount);
            if (Environment.GetEnvironmentVariable("DANMUPANEL_UPDATE_SCREENSHOTS") is { Length: > 0 } output)
            {
                Directory.CreateDirectory(output);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                Dispatcher.UIThread.RunJobs();
                using var frame = first.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(output, "update-available.png"));
            }
            settings.Close();
            await settingsTask;
            Assert.Null(coordinator.CurrentWindow);
        }
        finally
        {
            settings.Close();
            main.Close();
        }
    }

    /// <summary>自动结果在模态操作期间保留，恢复交互后只显示一次。</summary>
    [AvaloniaFact]
    public async Task AutomaticWindowWaitsUntilModalInteractionEnds()
    {
        await using var context = new AppUpdateTestContext();
        var main = new Window();
        var blocked = true;
        await using var coordinator = new AppUpdateWindowCoordinator(main, context.Model, () => blocked);
        try
        {
            main.Show();
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await coordinator.CheckOnStartupAsync();
            Assert.Null(coordinator.CurrentWindow);
            blocked = false;
            coordinator.RefreshPresentation();
            var window = Assert.IsType<AppUpdateWindow>(coordinator.CurrentWindow);
            Assert.Same(main, window.Owner);
            blocked = true;
            coordinator.RefreshPresentation();
            Assert.Null(coordinator.CurrentWindow);
            blocked = false;
            coordinator.RefreshPresentation();
            var reopened = Assert.IsType<AppUpdateWindow>(coordinator.CurrentWindow);
            reopened.Close();
            coordinator.RefreshPresentation();
            Assert.Null(coordinator.CurrentWindow);
            Assert.Equal(1, context.Service.CallCount);
        }
        finally
        {
            main.Close();
        }
    }

    /// <summary>窗口关闭按钮取消当前请求，迟到结果不会重新显示。</summary>
    [AvaloniaFact]
    public async Task ClosingWindowRejectsLateResults()
    {
        await using var context = new AppUpdateTestContext();
        var owner = new Window();
        await using var coordinator = new AppUpdateWindowCoordinator(owner, context.Model);
        try
        {
            owner.Show();
            var check = coordinator.CheckManuallyAsync(owner);
            var window = Assert.IsType<AppUpdateWindow>(coordinator.CurrentWindow);
            Pump(window);
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CloseUpdateButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(coordinator.CurrentWindow);
            Assert.True(context.Service.CancellationToken.IsCancellationRequested);
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await check;
            Assert.Null(coordinator.CurrentWindow);
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>主窗口关闭后，设置所属的提示也会关闭并取消请求。</summary>
    [AvaloniaFact]
    public async Task OwnerCloseCancelsAllPresentation()
    {
        await using var context = new AppUpdateTestContext();
        var owner = new Window();
        await using var coordinator = new AppUpdateWindowCoordinator(owner, context.Model);
        try
        {
            owner.Show();
            var check = coordinator.CheckManuallyAsync(owner);
            owner.Close();
            Assert.True(context.Service.CancellationToken.IsCancellationRequested);
            Assert.Null(coordinator.CurrentWindow);
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await check;
            Assert.Null(coordinator.CurrentWindow);
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>设置的子模态关闭前不展示手动结果，关闭后自动恢复提示。</summary>
    [AvaloniaFact]
    public async Task ManualResultWaitsForASettingsChildDialog()
    {
        await using var context = new AppUpdateTestContext();
        var main = new Window();
        var settings = new Window();
        var child = new Window();
        await using var coordinator = new AppUpdateWindowCoordinator(main, context.Model, () => true);
        try
        {
            main.Show();
            var settingsTask = settings.ShowDialog(main);
            var check = coordinator.CheckManuallyAsync(settings);
            Assert.NotNull(coordinator.CurrentWindow);
            var childTask = child.ShowDialog(settings);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(coordinator.CurrentWindow);
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await check;
            Assert.Null(coordinator.CurrentWindow);
            child.Close();
            await childTask;
            Dispatcher.UIThread.RunJobs();
            Assert.Same(settings, Assert.IsType<AppUpdateWindow>(coordinator.CurrentWindow).Owner);
            settings.Close();
            await settingsTask;
        }
        finally
        {
            context.Service.Response.TrySetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
            child.Close();
            settings.Close();
            main.Close();
        }
    }

    /// <summary>设置关闭会取消检查，即使发布源忽略取消也不会重开窗口。</summary>
    [AvaloniaFact]
    public async Task ClosingSettingsCancelsItsPendingCheck()
    {
        await using var context = new AppUpdateTestContext();
        var main = new Window();
        var settings = new Window();
        await using var coordinator = new AppUpdateWindowCoordinator(main, context.Model);
        try
        {
            main.Show();
            var settingsTask = settings.ShowDialog(main);
            var check = coordinator.CheckManuallyAsync(settings);
            settings.Close();
            await settingsTask;
            Assert.True(context.Service.CancellationToken.IsCancellationRequested);
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await check;
            Assert.Null(coordinator.CurrentWindow);
        }
        finally
        {
            context.Service.Response.TrySetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
            settings.Close();
            main.Close();
        }
    }

    /// <summary>主窗关闭准备期间隐藏设置所属的更新提示，保存失败恢复后可再显示。</summary>
    [AvaloniaFact]
    public async Task MainWindowShutdownBlocksTheSettingsPresentation()
    {
        await using var context = new AppUpdateTestContext();
        var main = new Window();
        var settings = new Window();
        await using var coordinator = new AppUpdateWindowCoordinator(main, context.Model);
        try
        {
            main.Show();
            var settingsTask = settings.ShowDialog(main);
            var check = coordinator.CheckManuallyAsync(settings);
            main.IsEnabled = false;
            Assert.Null(coordinator.CurrentWindow);
            context.Service.Response.SetResult(new(AppUpdateCheckStatus.UPDATE_AVAILABLE, AppUpdateTestContext.Update));
            await check;
            Assert.Null(coordinator.CurrentWindow);
            main.IsEnabled = true;
            Assert.NotNull(coordinator.CurrentWindow);
            settings.Close();
            await settingsTask;
        }
        finally
        {
            context.Service.Response.TrySetResult(new(AppUpdateCheckStatus.NO_RELEASE, null));
            settings.Close();
            main.Close();
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
