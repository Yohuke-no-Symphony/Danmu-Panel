using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证 macOS 菜单位置选项的即时生效、持久化和跨平台显示。</summary>
public sealed class MenuPositionSettingsTests
{
    /// <summary>缺少新增字段的旧配置默认保留 macOS 系统菜单栏。</summary>
    [Fact]
    public void ExistingConfigurationDefaultsToTheSystemMenu()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");
        var settings = store.Load().Settings;
        Assert.False(settings.UseWindowMenuOnMac);
        Assert.Equal("ja-JP", settings.Language);
    }

    /// <summary>勾选后无需保存整个设置页便切换并落盘，重建工作台读取同一偏好。</summary>
    [AvaloniaFact]
    public async Task PreferenceAppliesImmediatelyAndSurvivesReopeningTheWorkbench()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration
        {
            Settings = new() { CompactDanmuDisplay = false, CompactDanmuWrap = true, CompactSuperChatWrap = false }
        };
        await using var runtime = new PanelRuntime(store, configuration);
        var view = new SettingsView(runtime, new TextBlock(), supportsNativeMenus: true);
        var settingsWindow = new Window { Content = view, Width = 1020, Height = 760 };
        var shell = CreateShell(directory.Path, configuration.Settings);
        ShellWindow? reopened = null;
        view.WindowMenuOnMacChanged += value => shell.UseWindowMenuOnMac = value;
        var fullSettingsUpdates = 0;
        runtime.Changed += _ => fullSettingsUpdates++;
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            settingsWindow.Show();
            Pump(shell);
            Pump(settingsWindow);
            var checkbox = FindCheckbox(view);
            Assert.True(checkbox.IsVisible);
            Assert.False(checkbox.IsChecked);
            var root = NativeMenu.GetMenu(shell)!;
            checkbox.IsChecked = true;
            Assert.True(shell.UseWindowMenuOnMac);
            Assert.Same(root, NativeMenu.GetMenu(shell));
            Assert.All(root.Items.OfType<NativeMenuItem>(), item => Assert.False(item.IsVisible));
            Assert.True(FindWindowMenu(shell).IsVisible);
            await view.PendingMenuPreferenceSave;

            var restored = store.Load().Settings;
            Assert.True(restored.UseWindowMenuOnMac);
            Assert.False(restored.CompactDanmuDisplay);
            Assert.True(restored.CompactDanmuWrap);
            Assert.False(restored.CompactSuperChatWrap);
            Assert.Equal(0, fullSettingsUpdates);
            settingsWindow.Close();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();

            reopened = CreateShell(directory.Path, restored);
            await reopened.InitializeLayoutAsync();
            reopened.Show();
            Pump(reopened);
            Assert.True(FindWindowMenu(reopened).IsVisible);
            Assert.All(NativeMenu.GetMenu(reopened)!.Items.OfType<NativeMenuItem>(), item => Assert.False(item.IsVisible));
        }
        finally
        {
            await view.PendingMenuPreferenceSave;
            settingsWindow.Close();
            await shell.CloseAsync();
            if (reopened is not null)
            {
                await reopened.CloseAsync();
            }
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>平台不支持原生菜单时隐藏切换项，并在保存时保留已有 macOS 偏好。</summary>
    [AvaloniaFact]
    public async Task NonNativePlatformHidesTheOptionAndPreservesItsSavedPreference()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration { Settings = new() { UseWindowMenuOnMac = true } };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var view = new SettingsView(runtime, new TextBlock(), supportsNativeMenus: false);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        try
        {
            window.Show();
            Pump(window);
            var checkbox = FindCheckbox(view);
            Assert.False(checkbox.IsVisible);
            Assert.True(checkbox.IsChecked);
            Assert.True(await view.SaveAsync());
            Assert.True(runtime.ConfigurationStore.Load().Settings.UseWindowMenuOnMac);
        }
        finally
        {
            window.Close();
        }
    }

    private static CheckBox FindCheckbox(Control view)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<CheckBox>(), check => check.Name == "UseWindowMenuOnMacCheckBox");
    }

    private static Menu FindWindowMenu(ShellWindow shell)
    {
        return Assert.Single(shell.GetVisualDescendants().OfType<Menu>(), menu => menu.Classes.Contains("workbenchMenu"));
    }

    private static ShellWindow CreateShell(string directory, PanelSettings settings)
    {
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        return new(panels, new TextBlock(), directory, supportsNativeMenus: true)
        {
            UseWindowMenuOnMac = settings.UseWindowMenuOnMac
        };
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
