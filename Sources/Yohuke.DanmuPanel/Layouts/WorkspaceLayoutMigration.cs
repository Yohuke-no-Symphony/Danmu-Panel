namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkspaceLayoutMigration
{
    internal static WorkspaceLayoutFile Upgrade(WorkspaceLayoutFile file)
    {
        return file.Version switch
        {
            1 => file with
            {
                Version = WorkspaceLayoutFile.CURRENT_VERSION,
                CurrentLayout = AddHiddenTestPanel(file.CurrentLayout),
                UserPresets = file.UserPresets.Select(preset => preset with
                {
                    Layout = AddHiddenTestPanel(preset.Layout)
                }).ToList()
            },
            _ => throw new ArgumentOutOfRangeException(nameof(file))
        };
    }

    private static WorkspaceLayoutSnapshot AddHiddenTestPanel(WorkspaceLayoutSnapshot snapshot)
    {
        return snapshot with { HiddenPanelIds = [.. snapshot.HiddenPanelIds, WorkbenchPanelIds.TEST_MESSAGES] };
    }
}
