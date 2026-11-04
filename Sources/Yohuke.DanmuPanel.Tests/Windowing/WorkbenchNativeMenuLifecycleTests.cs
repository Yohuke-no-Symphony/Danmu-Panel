using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证真实菜单附加属性上的根身份、窗口隔离及延迟图标导出。</summary>
public sealed class WorkbenchNativeMenuLifecycleTests
{
    /// <summary>探针拒绝已导出菜单被替换或清空，确保能够发现启动时的原始故障。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExporterRejectsReplacingAnExportedRoot(bool clearMenu)
    {
        var window = new Window();
        using var exporter = new NativeMenuExporterProbe(window);
        try
        {
            NativeMenu.SetMenu(window, new());
            var error = Assert.Throws<ArgumentException>(() => NativeMenu.SetMenu(window, clearMenu ? null : new()));
            Assert.Equal("menu", error.ParamName);
            Assert.StartsWith("The menu being updated does not match.", error.Message);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>初始化、语言切换、布局结构及勾选更新始终保留窗口首次导出的菜单根。</summary>
    [AvaloniaFact]
    public async Task StartupLanguageAndLayoutChangesKeepTheExportedRoot()
    {
        var directory = CreateDirectory();
        var language = Localization.SelectedLanguageID;
        var shell = CreateShell(directory);
        var errors = new List<Exception>();
        shell.OperationFailed += errors.Add;
        using var exporter = new NativeMenuExporterProbe(shell);
        var root = Assert.IsType<NativeMenu>(exporter.Root);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            Assert.Same(root, NativeMenu.GetMenu(shell));
            Assert.Equal(1, exporter.AssignmentCount);

            foreach (var languageId in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                Localization.SetLanguage(languageId);
                exporter.ExportPendingChanges();
                Assert.Equal(Localization.Get("Shell.Menu.Application"), ((NativeMenuItem)root.Items[0]).Header);
                Assert.Equal(Localization.Get("Shell.Settings"), FindItem(root, Localization.Get("Shell.Settings")).Header);
            }

            var giftTitle = WorkbenchDockPanel.GetLocalizedTitle(WorkbenchPanelIds.GIFT);
            var giftItem = FindItem(root, giftTitle);
            shell.Layouts.HidePanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            Assert.Same(giftItem, FindItem(root, giftTitle));
            Assert.False(giftItem.IsChecked);
            shell.ShowPanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            Assert.True(giftItem.IsChecked);

            var presetId = shell.Layouts.SaveAs("菜单测试布局");
            exporter.ExportPendingChanges();
            var presetItem = FindItem(root, "菜单测试布局");
            Assert.True(presetItem.IsChecked);
            Assert.True(shell.Layouts.Rename(presetId, "已重命名布局"));
            exporter.ExportPendingChanges();
            Assert.Same(presetItem, FindItem(root, "已重命名布局"));
            shell.Layouts.RestoreDefault();
            Assert.True(shell.Layouts.Delete(presetId));
            exporter.ExportPendingChanges();
            Assert.DoesNotContain(EnumerateItems(root), item => item.Header == "已重命名布局");
            Assert.Same(root, NativeMenu.GetMenu(shell));
            Assert.Equal(1, exporter.AssignmentCount);
            Assert.Empty(errors);
        }
        finally
        {
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Localization.SetLanguage(language);
            Directory.Delete(directory, true);
        }
    }

    /// <summary>浮窗独占菜单树并共享命令；关闭后根保持不变且停止接收菜单刷新。</summary>
    [AvaloniaFact]
    public async Task FloatingWindowOwnsItsTreeAndStopsUpdatingAfterClose()
    {
        var directory = CreateDirectory();
        var shell = CreateShell(directory);
        using var mainExporter = new NativeMenuExporterProbe(shell);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            shell.FloatPanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            var floating = Assert.Single(shell.Layouts.FloatingWindows);
            using var floatingExporter = new NativeMenuExporterProbe(floating);
            var mainRoot = Assert.IsType<NativeMenu>(mainExporter.Root);
            var floatingRoot = Assert.IsType<NativeMenu>(floatingExporter.Root);
            var mainObjects = EnumerateObjects(mainRoot).ToHashSet();
            Assert.DoesNotContain(EnumerateObjects(floatingRoot), mainObjects.Contains);
            var mainSettings = FindItem(mainRoot, Localization.Get("Shell.Settings"));
            var floatingSettings = FindItem(floatingRoot, Localization.Get("Shell.Settings"));
            Assert.Same(mainSettings.Command, floatingSettings.Command);
            Assert.Same(mainSettings.Icon, floatingSettings.Icon);
            var requests = 0;
            shell.SettingsRequested += () => requests++;
            floatingSettings.Command!.Execute(null);
            Assert.Equal(1, requests);

            floating.Close();
            Pump(shell);
            Assert.True(floatingExporter.IsClosed);
            var frozenObjects = EnumerateObjects(floatingRoot).ToArray();
            shell.Layouts.SaveAs("浮窗关闭后布局");
            mainExporter.ExportPendingChanges();
            Assert.Equal(frozenObjects, EnumerateObjects(floatingRoot));
            Assert.Same(floatingRoot, NativeMenu.GetMenu(floating));
            Assert.Equal(1, floatingExporter.AssignmentCount);
            Assert.Equal(1, mainExporter.AssignmentCount);
        }
        finally
        {
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>新建预设后立即退出，关闭事件中的图标仍可导出，排队任务结束后才释放缓存。</summary>
    [AvaloniaFact]
    public async Task ClosingAfterPresetCreationKeepsIconsAliveUntilQueuedExportsComplete()
    {
        var directory = CreateDirectory();
        var shell = CreateShell(directory);
        using var exporter = new NativeMenuExporterProbe(shell);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var root = Assert.IsType<NativeMenu>(exporter.Root);
            shell.Layouts.SaveAs("退出前布局");
            var icon = Assert.IsAssignableFrom<Avalonia.Media.Imaging.Bitmap>(FindItem(root, "退出前布局").Icon);
            var aliveAtClose = false;
            shell.Closed += (_, _) =>
            {
                using var stream = new MemoryStream();
                icon.Save(stream);
                aliveAtClose = stream.Length > 0;
            };
            await shell.CloseAsync();
            Assert.True(aliveAtClose);
            Dispatcher.UIThread.RunJobs();
            Assert.False(exporter.HasPendingChanges);
            Assert.Same(root, NativeMenu.GetMenu(shell));
            Assert.Equal(1, exporter.AssignmentCount);
            using var stream = new MemoryStream();
            Assert.NotNull(Record.Exception(() => icon.Save(stream)));
        }
        finally
        {
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>模态窗口禁用业务菜单命令，关闭后恢复。</summary>
    [AvaloniaFact]
    public async Task ModalWindowTemporarilyDisablesSharedCommands()
    {
        var directory = CreateDirectory();
        var shell = CreateShell(directory);
        var dialog = new Window();
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var command = FindItem(NativeMenu.GetMenu(shell)!, Localization.Get("Shell.Settings")).Command!;
            Assert.True(command.CanExecute(null));
            var pending = shell.ShowModalAsync<bool>(dialog);
            Assert.False(command.CanExecute(null));
            dialog.Close(true);
            Assert.True(await pending);
            Assert.True(command.CanExecute(null));
        }
        finally
        {
            dialog.Close();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>原生与窗口菜单往返切换、预设重建及浮窗关闭均保持已导出的根与当前模式。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MenuModesKeepEveryExportedRootAndApplyToNewFloatingWindows(bool startsWithWindowMenu)
    {
        var directory = CreateDirectory();
        var shell = CreateShell(directory);
        shell.UseWindowMenuOnMac = startsWithWindowMenu;
        using var mainExporter = new NativeMenuExporterProbe(shell);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            var mainRoot = Assert.IsType<NativeMenu>(mainExporter.Root);
            AssertMenuMode(shell, mainRoot, startsWithWindowMenu);
            shell.FloatPanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            var floating = Assert.Single(shell.Layouts.FloatingWindows);
            using var floatingExporter = new NativeMenuExporterProbe(floating);
            var floatingRoot = Assert.IsType<NativeMenu>(floatingExporter.Root);
            Assert.All(floatingRoot.Items.OfType<NativeMenuItem>(), item => Assert.Equal(!startsWithWindowMenu, item.IsVisible));

            foreach (var windowMode in new[] { !startsWithWindowMenu, startsWithWindowMenu })
            {
                shell.UseWindowMenuOnMac = windowMode;
                shell.Layouts.SaveAs(windowMode ? "窗口菜单布局" : "系统菜单布局");
                mainExporter.ExportPendingChanges();
                floatingExporter.ExportPendingChanges();
                AssertMenuMode(shell, mainRoot, windowMode);
                Assert.All(floatingRoot.Items.OfType<NativeMenuItem>(), item => Assert.Equal(!windowMode, item.IsVisible));
                Assert.Same(mainRoot, NativeMenu.GetMenu(shell));
                Assert.Same(floatingRoot, NativeMenu.GetMenu(floating));
                Assert.Equal(1, mainExporter.AssignmentCount);
                Assert.Equal(1, floatingExporter.AssignmentCount);
            }

            floating.Close();
            Pump(shell);
            var frozenVisibility = floatingRoot.Items.OfType<NativeMenuItem>().Select(item => item.IsVisible).ToArray();
            shell.UseWindowMenuOnMac = !startsWithWindowMenu;
            mainExporter.ExportPendingChanges();
            Assert.Equal(frozenVisibility, floatingRoot.Items.OfType<NativeMenuItem>().Select(item => item.IsVisible));
            AssertMenuMode(shell, mainRoot, !startsWithWindowMenu);
        }
        finally
        {
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    /// <summary>不支持原生菜单的平台始终显示窗口菜单，不受保存的 macOS 偏好影响。</summary>
    [AvaloniaFact]
    public async Task NonNativePlatformAlwaysKeepsTheWindowMenuVisible()
    {
        var directory = CreateDirectory();
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        var shell = new ShellWindow(panels, new TextBlock(), directory, supportsNativeMenus: false);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            Pump(shell);
            foreach (var preference in new[] { false, true, false })
            {
                shell.UseWindowMenuOnMac = preference;
                Assert.True(FindWindowMenu(shell).IsVisible);
                Assert.Null(NativeMenu.GetMenu(shell));
            }
        }
        finally
        {
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
            Directory.Delete(directory, true);
        }
    }

    private static void AssertMenuMode(ShellWindow shell, NativeMenu nativeMenu, bool useWindowMenu)
    {
        Assert.Equal(useWindowMenu, FindWindowMenu(shell).IsVisible);
        Assert.NotEmpty(nativeMenu.Items);
        Assert.All(nativeMenu.Items.OfType<NativeMenuItem>(), item => Assert.Equal(!useWindowMenu, item.IsVisible));
    }

    private static Menu FindWindowMenu(ShellWindow shell)
    {
        return Assert.Single(shell.GetVisualDescendants().OfType<Menu>(), menu => menu.Classes.Contains("workbenchMenu"));
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-panel-native-menu-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ShellWindow CreateShell(string directory)
    {
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        return new(panels, new Button { Content = "Listen" }, directory, supportsNativeMenus: true);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static NativeMenuItem FindItem(NativeMenu root, string header)
    {
        return EnumerateItems(root).Single(item => item.Header == header);
    }

    private static IEnumerable<NativeMenuItem> EnumerateItems(NativeMenu root)
    {
        return EnumerateObjects(root).OfType<NativeMenuItem>();
    }

    private static IEnumerable<AvaloniaObject> EnumerateObjects(NativeMenu menu)
    {
        yield return menu;
        foreach (var item in menu.Items)
        {
            yield return item;
            if (item is NativeMenuItem { Menu: { } child })
            {
                foreach (var descendant in EnumerateObjects(child))
                {
                    yield return descendant;
                }
            }
        }
    }
}
