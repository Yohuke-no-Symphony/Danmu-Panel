namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkspaceLayoutMigration
{
    internal static WorkspaceLayoutFile Upgrade(WorkspaceLayoutFile file)
    {
        if (file.Version is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(file));
        }

        var addedPanels = WorkbenchPanelIds.All.Except(WorkbenchPanelIds.ForVersion(file.Version)).ToArray();
        return file with
        {
            Version = WorkspaceLayoutFile.CURRENT_VERSION,
            CurrentLayout = AddHiddenPanels(file.CurrentLayout, addedPanels),
            UserPresets = file.UserPresets.Select(preset => preset with
            {
                Layout = AddHiddenPanels(preset.Layout, addedPanels)
            }).ToList()
        };
    }

    private static WorkspaceLayoutSnapshot AddHiddenPanels(WorkspaceLayoutSnapshot snapshot, string[] panelIds)
    {
        return snapshot with { HiddenPanelIds = [.. snapshot.HiddenPanelIds, .. panelIds] };
    }
}
