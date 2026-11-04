using System.Text.Json;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Tests.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Logging;

/// <summary>验证新增日志面板时保留旧版布局与自定义预设。</summary>
public sealed class LogLayoutMigrationTests
{
    /// <summary>旧版布局只增加缺失的隐藏面板，保留停靠、浮窗、焦点与预设。</summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacyLayoutsPreserveGeometryAndPresets(int version)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var snapshot = WorkspaceLayoutPresets.Create(WorkspaceLayoutPresets.DEFAULT) with
        {
            HiddenPanelIds = version == 1 ? [] : [WorkbenchPanelIds.TEST_MESSAGES]
        };
        var legacy = new WorkspaceLayoutFile
        {
            Version = version,
            CurrentPresetId = "custom",
            CurrentLayout = snapshot,
            UserPresets = [new() { Id = "custom", Name = "Existing layout", Layout = snapshot }]
        };
        var store = new WorkspaceLayoutStore(directory.Path);
        await File.WriteAllTextAsync(store.FilePath, JsonSerializer.Serialize(legacy));

        var migrated = await store.LoadAsync();

        Assert.Equal(3, migrated.Version);
        Assert.Equal("custom", migrated.CurrentPresetId);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Main), JsonSerializer.Serialize(migrated.CurrentLayout.Main));
        Assert.Equal(snapshot.FocusedPanelId, migrated.CurrentLayout.FocusedPanelId);
        Assert.Equal(new[] { WorkbenchPanelIds.TEST_MESSAGES, "log" }, migrated.CurrentLayout.HiddenPanelIds);
        var preset = Assert.Single(migrated.UserPresets);
        Assert.Equal("Existing layout", preset.Name);
        Assert.Equal(migrated.CurrentLayout.HiddenPanelIds, preset.Layout.HiddenPanelIds);
        Assert.True(WorkspaceLayoutValidator.TryValidate(migrated, out _));
        var loadedAgain = await store.LoadAsync();
        Assert.Equal(migrated.CurrentLayout.HiddenPanelIds, loadedAgain.CurrentLayout.HiddenPanelIds);
        Assert.Single(Directory.GetFiles(store.BackupDirectory));
    }

    /// <summary>缺少旧版已有面板的文件在迁移前仍被拒绝。</summary>
    [AvaloniaFact]
    public void VersionTwoMissingTestPanelIsRejected()
    {
        var malformed = WorkspaceLayoutPresets.CreateDefaultFile() with
        {
            Version = 2,
            CurrentLayout = WorkspaceLayoutPresets.Create(WorkspaceLayoutPresets.DEFAULT) with { HiddenPanelIds = [] }
        };

        Assert.Throws<InvalidDataException>(() => WorkspaceLayoutSerializer.Deserialize(JsonSerializer.Serialize(malformed)));
    }
}
