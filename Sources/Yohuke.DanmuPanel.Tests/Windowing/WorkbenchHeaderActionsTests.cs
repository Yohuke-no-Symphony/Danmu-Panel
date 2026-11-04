using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证面板操作模板在停靠和浮窗标题中以正确的面板为上下文创建。</summary>
public sealed class WorkbenchHeaderActionsTests
{
    /// <summary>浮动会创建独立标题按钮，并保留原面板实例作为数据上下文。</summary>
    [AvaloniaFact]
    public async Task HeaderActionsAreRecreatedWhenThePanelFloats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-panel-header-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var view = new HeaderActionsTestView();
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[WorkbenchPanelIds.GIFT] = view;
        var shell = new ShellWindow(panels, new TextBlock(), directory);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var dockedAction = FindAction(shell);
            Assert.Same(view, dockedAction.DataContext);
            shell.FloatPanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            var floating = Assert.Single(shell.Layouts.FloatingWindows);
            Pump(floating);
            var floatingAction = FindAction(floating);
            Assert.Same(view, floatingAction.DataContext);
            Assert.NotSame(dockedAction, floatingAction);
            Assert.DoesNotContain(shell.GetVisualDescendants().OfType<Button>(), button => button.Name == "TestHeaderAction");
        }
        finally
        {
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    private static Button FindAction(Window window)
    {
        return Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "TestHeaderAction");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
