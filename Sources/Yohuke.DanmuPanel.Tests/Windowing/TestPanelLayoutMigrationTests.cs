using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证隐藏测试面板的布局迁移及显隐持久化。</summary>
public sealed class TestPanelLayoutMigrationTests
{
    /// <summary>版本 1 的停靠、浮窗、焦点和用户预设原样保留，只追加隐藏测试面板。</summary>
    [AvaloniaFact]
    public async Task VersionOneLayoutsKeepGeometryAndPresetsWhenAddingTheHiddenTestPanel()
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var snapshot = new WorkspaceLayoutSnapshot
        {
            Main = new()
            {
                Kind = WorkspaceLayoutNodeKind.Tabs,
                PanelIds = [WorkbenchPanelIds.WORKSPACE, WorkbenchPanelIds.DANMU, WorkbenchPanelIds.SUPER_CHAT],
                ActivePanelId = WorkbenchPanelIds.SUPER_CHAT
            },
            FloatingWindows =
            [
                new()
                {
                    Bounds = new() { X = 70, Y = 90, Width = 460, Height = 620, Scaling = 2 },
                    Layout = new() { Kind = WorkspaceLayoutNodeKind.Tabs, PanelIds = [WorkbenchPanelIds.GIFT], ActivePanelId = WorkbenchPanelIds.GIFT }
                }
            ],
            HiddenPanelIds = [WorkbenchPanelIds.GUARD],
            FocusedPanelId = WorkbenchPanelIds.GIFT
        };
        var legacy = new WorkspaceLayoutFile
        {
            Version = 1, CurrentPresetId = "custom", CurrentLayout = snapshot,
            UserPresets = [new() { Id = "custom", Name = "Existing layout", Layout = snapshot }]
        };
        var path = Path.Combine(directory.Path, "layouts.json");
        await File.WriteAllTextAsync(path, WorkspaceLayoutSerializer.Serialize(legacy));
        var store = new WorkspaceLayoutStore(directory.Path);
        var migrated = await store.LoadAsync();
        Assert.Equal(3, migrated.Version);
        Assert.Equal("custom", migrated.CurrentPresetId);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Main), JsonSerializer.Serialize(migrated.CurrentLayout.Main));
        Assert.Equal(JsonSerializer.Serialize(snapshot.FloatingWindows), JsonSerializer.Serialize(migrated.CurrentLayout.FloatingWindows));
        Assert.Equal(snapshot.FocusedPanelId, migrated.CurrentLayout.FocusedPanelId);
        Assert.Equal(new[] { WorkbenchPanelIds.GUARD, WorkbenchPanelIds.TEST_MESSAGES, WorkbenchPanelIds.LOG }, migrated.CurrentLayout.HiddenPanelIds);
        var preset = Assert.Single(migrated.UserPresets);
        Assert.Equal("Existing layout", preset.Name);
        Assert.Equal(migrated.CurrentLayout.HiddenPanelIds, preset.Layout.HiddenPanelIds);
        Assert.True(WorkspaceLayoutValidator.TryValidate(migrated, out _));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(3, saved.RootElement.GetProperty("Version").GetInt32());
        var backup = Assert.Single(Directory.GetFiles(store.BackupDirectory));
        using var previous = JsonDocument.Parse(await File.ReadAllTextAsync(backup));
        Assert.Equal(1, previous.RootElement.GetProperty("Version").GetInt32());
        var loadedAgain = await store.LoadAsync();
        Assert.Single(loadedAgain.CurrentLayout.HiddenPanelIds, id => id == WorkbenchPanelIds.TEST_MESSAGES);
        Assert.Single(Directory.GetFiles(store.BackupDirectory));
    }

    /// <summary>迁移前先验证旧布局，缺少真实面板的损坏文件不会被当作合法版本升级。</summary>
    [AvaloniaFact]
    public void IncompleteLegacyLayoutIsRejectedBeforeMigration()
    {
        var malformed = JsonSerializer.Serialize(new WorkspaceLayoutFile
        {
            Version = 1,
            CurrentLayout = new() { HiddenPanelIds = [WorkbenchPanelIds.WORKSPACE] }
        });
        Assert.Throws<InvalidDataException>(() => WorkspaceLayoutSerializer.Deserialize(malformed));
    }

    /// <summary>默认布局隐藏测试面板，通过视图菜单打开后可浮动并跨启动恢复。</summary>
    [AvaloniaFact]
    public async Task HiddenTestPanelCanBeOpenedFromTheMenuAndRestoredAsAFloatingWindow()
    {
        using var directory = new MessageStoreTestDirectory();
        var first = CreateShell(directory.Path);
        try
        {
            await first.InitializeLayoutAsync();
            first.Show();
            Pump(first);
            Assert.False(first.Layouts.IsVisible(WorkbenchPanelIds.TEST_MESSAGES));
            Assert.All(WorkbenchPanelIds.ForVersion(1), id => Assert.True(first.Layouts.IsVisible(id)));
            var root = NativeMenu.GetMenu(first)!;
            var item = root.Items.OfType<NativeMenuItem>().SelectMany(group => group.Menu?.Items.OfType<NativeMenuItem>() ?? [])
                .Single(entry => entry.Header == Localization.Get("Workbench.Panel.TestMessages"));
            item.Command!.Execute(null);
            Pump(first);
            Assert.True(first.Layouts.IsVisible(WorkbenchPanelIds.TEST_MESSAGES));
            first.FloatPanel(WorkbenchPanelIds.TEST_MESSAGES);
            Pump(first);
            Assert.Single(first.Layouts.FloatingWindows);
        }
        finally
        {
            await first.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }

        var second = CreateShell(directory.Path);
        try
        {
            await second.InitializeLayoutAsync();
            second.Show();
            Pump(second);
            Assert.True(second.Layouts.IsVisible(WorkbenchPanelIds.TEST_MESSAGES));
            Assert.Single(second.Layouts.FloatingWindows);
            second.Layouts.RestoreDefault();
            Pump(second);
            Assert.False(second.Layouts.IsVisible(WorkbenchPanelIds.TEST_MESSAGES));
            Assert.All(WorkbenchPanelIds.ForVersion(1), id => Assert.True(second.Layouts.IsVisible(id)));
        }
        finally
        {
            await second.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ShellWindow CreateShell(string directory)
    {
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        return new(panels, new TextBlock(), directory, supportsNativeMenus: true);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
