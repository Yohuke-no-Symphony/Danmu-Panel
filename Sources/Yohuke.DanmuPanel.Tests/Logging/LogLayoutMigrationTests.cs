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
        var snapshot = CreateLegacySnapshot(version);
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

        Assert.Equal(WorkspaceLayoutFile.CURRENT_VERSION, migrated.Version);
        Assert.Equal("custom", migrated.CurrentPresetId);
        Assert.Equal(JsonSerializer.Serialize(snapshot.Main), JsonSerializer.Serialize(migrated.CurrentLayout.Main));
        Assert.Equal(snapshot.FocusedPanelId, migrated.CurrentLayout.FocusedPanelId);
        Assert.Equal(new[] { WorkbenchPanelIds.TEST_MESSAGES, WorkbenchPanelIds.LOG, WorkbenchPanelIds.RED_POCKET }, migrated.CurrentLayout.HiddenPanelIds);
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
            CurrentLayout = CreateLegacySnapshot(2) with { HiddenPanelIds = [] }
        };

        Assert.Throws<InvalidDataException>(() => WorkspaceLayoutSerializer.Deserialize(JsonSerializer.Serialize(malformed)));
    }

    private static WorkspaceLayoutSnapshot CreateLegacySnapshot(int version)
    {
        var snapshot = WorkspaceLayoutPresets.Create(WorkspaceLayoutPresets.DEFAULT);
        var main = snapshot.Main!;
        var events = main.Children[2];
        var gift = events.Children[1];
        return snapshot with
        {
            Main = main with
            {
                Children =
                [
                    main.Children[0],
                    main.Children[1],
                    events with
                    {
                        Children =
                        [
                            events.Children[0],
                            gift.Children[0] with { Proportion = gift.Proportion },
                            events.Children[2]
                        ]
                    }
                ]
            },
            HiddenPanelIds = version == 1 ? [] : [WorkbenchPanelIds.TEST_MESSAGES]
        };
    }
}
