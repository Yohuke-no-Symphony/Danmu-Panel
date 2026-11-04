using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Windowing;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证五面板工作台的真实 Dock 布局、浮窗置顶及退出持久化。</summary>
public sealed class ShellWindowTests
{
    /// <summary>五个面板唯一注册，主窗取消置顶后仍保留浮窗独立选择。</summary>
    [AvaloniaFact]
    public async Task FivePanelsFloatAndKeepIndependentTopmostState()
    {
        var directory = CreateDirectory();
        var shell = CreateShell(directory);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            Assert.Equal(7, shell.Layouts.PanelAdapters.Count);
            Assert.False(shell.Layouts.IsVisible(WorkbenchPanelIds.TEST_MESSAGES));
            Assert.True(WorkspaceLayoutValidator.TryValidate(shell.Layouts.CaptureFile(), out _));
            shell.FloatPanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            var gift = Assert.Single(shell.Layouts.FloatingWindows);
            WorkbenchWindowTopmost.SetIsIndependentlyTopmost(gift, true);
            shell.FloatPanel(WorkbenchPanelIds.GUARD);
            Pump(shell);
            var guard = shell.Layouts.FloatingWindows.Single(window => !ReferenceEquals(window, gift));
            Assert.True(gift.Topmost);
            Assert.False(guard.Topmost);
            shell.Topmost = true;
            Assert.True(gift.Topmost);
            Assert.True(guard.Topmost);
            shell.Topmost = false;
            Assert.True(gift.Topmost);
            Assert.False(guard.Topmost);
        }
        finally
        {
            await shell.CloseAsync();
            Assert.Empty(shell.Layouts.FloatingWindows);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>隐藏状态与命名布局跨启动恢复，恢复默认后重新显示全部面板。</summary>
    [AvaloniaFact]
    public async Task CustomLayoutAndHiddenPanelSurviveRestart()
    {
        var directory = CreateDirectory();
        var first = CreateShell(directory);
        string presetId;
        try
        {
            await first.InitializeLayoutAsync();
            first.Show();
            Pump(first);
            first.Layouts.HidePanel(WorkbenchPanelIds.GIFT);
            presetId = first.Layouts.SaveAs("测试布局");
            await first.CloseAsync();
        }
        catch
        {
            await first.CloseAsync();
            Directory.Delete(directory, true);
            throw;
        }

        var second = CreateShell(directory);
        try
        {
            await second.InitializeLayoutAsync();
            second.Show();
            Pump(second);
            Assert.Equal(presetId, second.Layouts.CurrentPresetId);
            Assert.False(second.Layouts.IsVisible(WorkbenchPanelIds.GIFT));
            Assert.Equal("测试布局", second.Layouts.Presets.Single(preset => preset.Id == presetId).Name);
            second.Layouts.RestoreDefault();
            Assert.All(WorkbenchPanelIds.ForVersion(1), id => Assert.True(second.Layouts.IsVisible(id)));
            Assert.False(second.Layouts.IsVisible(WorkbenchPanelIds.TEST_MESSAGES));
        }
        finally
        {
            await second.CloseAsync();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>应用保存失败时窗口保持可用，后续关闭能重新执行保存。</summary>
    [AvaloniaFact]
    public async Task FailedShutdownCanRetryWithoutLosingTheWindow()
    {
        var directory = CreateDirectory();
        var shell = CreateShell(directory);
        var attempts = 0;
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            shell.BeforeCloseAsync = () =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException(new IOException("storage failure")) : Task.CompletedTask;
            };
            await Assert.ThrowsAsync<IOException>(shell.CloseAsync);
            Assert.True(shell.IsVisible);
            Assert.True(shell.IsEnabled);
            Assert.False(shell.CanClose);
            await shell.CloseAsync();
            Assert.Equal(2, attempts);
            Assert.True(shell.CanClose);
            Assert.False(shell.IsVisible);
        }
        finally
        {
            shell.BeforeCloseAsync = null;
            await shell.CloseAsync();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>损坏布局被保留，并从最近有效备份恢复。</summary>
    [Fact]
    public async Task LayoutCorruptionRestoresTheLatestValidBackup()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new WorkspaceLayoutStore(directory);
            var original = await store.LoadAsync();
            await store.SaveAsync(original);
            await File.WriteAllTextAsync(store.FilePath, "{not valid json");
            var recovered = await store.LoadAsync();
            Assert.Equal(WorkspaceLayoutSerializer.Serialize(original), WorkspaceLayoutSerializer.Serialize(recovered));
            Assert.Single(Directory.GetFiles(store.CorruptDirectory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-panel-shell-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ShellWindow CreateShell(string directory)
    {
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        return new(panels, new Button { Content = "Listen" }, directory);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
