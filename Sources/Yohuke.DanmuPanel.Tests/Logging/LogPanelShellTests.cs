using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Logging;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Logging;

/// <summary>验证日志页的工作台入口、布局恢复与底部错误栏移除。</summary>
public sealed class LogPanelShellTests
{
    /// <summary>关闭操作失败只进入日志，日志页隐藏时不会在主窗口底部显示错误。</summary>
    [AvaloniaFact]
    public async Task OperationFailureIsLoggedWithoutShowingAFooter()
    {
        using var directory = new MessageStoreTestDirectory();
        using var log = new AppLogService();
        using var bridge = new AppLogRuntimeBridge(log);
        using var view = new LogPanelView(log);
        var shell = CreateShell(directory.Path, view);
        var failure = new TaskCompletionSource<Exception>();
        var error = new IOException("footer-regression-diagnostic");
        shell.OperationFailed += exception =>
        {
            StartupLog.Append(exception.Message, AppLogLevel.ERROR, "shell", exception);
            failure.TrySetResult(exception);
        };
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            shell.BeforeCloseAsync = () => Task.FromException(error);

            shell.Close();
            Assert.Same(error, await failure.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Pump(shell);

            Assert.False(shell.Layouts.IsVisible(WorkbenchPanelIds.LOG));
            Assert.Equal(error.Message, Assert.Single(log.Entries).Message);
            Assert.True(shell.IsEnabled);
            Assert.DoesNotContain(shell.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == error.Message);
        }
        finally
        {
            shell.BeforeCloseAsync = null;
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>日志页可从视图菜单打开、浮动并跨启动恢复，恢复默认后重新隐藏。</summary>
    [AvaloniaFact]
    public async Task LogMenuAndFloatingLayoutSurviveRestart()
    {
        using var directory = new MessageStoreTestDirectory();
        using var log = new AppLogService();
        using var firstView = new LogPanelView(log);
        var first = CreateShell(directory.Path, firstView);
        try
        {
            await first.InitializeLayoutAsync();
            first.Show();
            Pump(first);
            Assert.False(first.Layouts.IsVisible(WorkbenchPanelIds.LOG));
            var item = NativeMenu.GetMenu(first)!.Items.OfType<NativeMenuItem>()
                .SelectMany(group => group.Menu?.Items.OfType<NativeMenuItem>() ?? [])
                .Single(entry => entry.Header == Localization.Get("Workbench.Panel.Log"));
            item.Command!.Execute(null);
            Pump(first);
            Assert.True(first.Layouts.IsVisible(WorkbenchPanelIds.LOG));
            first.FloatPanel(WorkbenchPanelIds.LOG);
            Pump(first);
            Assert.Single(first.Layouts.FloatingWindows);
        }
        finally
        {
            await first.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }

        using var secondView = new LogPanelView(log);
        var second = CreateShell(directory.Path, secondView);
        try
        {
            await second.InitializeLayoutAsync();
            second.Show();
            Pump(second);
            Assert.True(second.Layouts.IsVisible(WorkbenchPanelIds.LOG));
            Assert.Single(second.Layouts.FloatingWindows);
            second.Layouts.RestoreDefault();
            Pump(second);
            Assert.False(second.Layouts.IsVisible(WorkbenchPanelIds.LOG));
        }
        finally
        {
            await second.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ShellWindow CreateShell(string directory, LogPanelView view)
    {
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[WorkbenchPanelIds.LOG] = view;
        return new(panels, new TextBlock(), directory, supportsNativeMenus: true);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
