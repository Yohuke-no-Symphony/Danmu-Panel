namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkspaceLayoutMigration
{
    internal static WorkspaceLayoutFile Upgrade(WorkspaceLayoutFile file)
    {
        if (file.Version is not (1 or 2 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(file));
        }

        var addedPanels = WorkbenchPanelIds.All.Except(WorkbenchPanelIds.ForVersion(file.Version)).ToArray();
        var current = AddHiddenPanels(file.CurrentLayout, addedPanels);
        if (file.CurrentPresetId == WorkspaceLayoutPresets.DEFAULT && IsOriginalDefault(file.CurrentLayout, file.Version))
        {
            current = current with
            {
                Main = WorkspaceLayoutPresets.Create(WorkspaceLayoutPresets.DEFAULT).Main,
                HiddenPanelIds = current.HiddenPanelIds.Where(id => id != WorkbenchPanelIds.RED_POCKET).ToList()
            };
        }
        return file with
        {
            Version = WorkspaceLayoutFile.CURRENT_VERSION,
            CurrentLayout = current,
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

    private static bool IsOriginalDefault(WorkspaceLayoutSnapshot snapshot, int version)
    {
        var expectedHidden = WorkbenchPanelIds.ForVersion(version).Except(WorkbenchPanelIds.ForVersion(1));
        return snapshot.FloatingWindows.Count == 0 && snapshot.HiddenPanelIds.ToHashSet(StringComparer.Ordinal).SetEquals(expectedHidden) &&
               MatchesNode(snapshot.Main, CreateOriginalDefaultMain());
    }

    private static bool MatchesNode(WorkspaceLayoutNode? actual, WorkspaceLayoutNode expected)
    {
        return actual is not null && actual.Kind == expected.Kind && actual.Orientation == expected.Orientation &&
               actual.Proportion.Equals(expected.Proportion) && actual.ActivePanelId == expected.ActivePanelId &&
               actual.PanelIds.SequenceEqual(expected.PanelIds) && actual.Children.Count == expected.Children.Count &&
               actual.Children.Zip(expected.Children).All(pair => MatchesNode(pair.First, pair.Second));
    }

    private static WorkspaceLayoutNode CreateOriginalDefaultMain()
    {
        return new()
        {
            Kind = WorkspaceLayoutNodeKind.Split,
            Orientation = WorkspaceLayoutOrientation.Horizontal,
            Proportion = 1,
            Children =
            [
                Tabs(0.18, WorkbenchPanelIds.WORKSPACE),
                Tabs(0.50, WorkbenchPanelIds.DANMU),
                new()
                {
                    Kind = WorkspaceLayoutNodeKind.Split,
                    Orientation = WorkspaceLayoutOrientation.Vertical,
                    Proportion = 0.32,
                    Children =
                    [
                        Tabs(0.4, WorkbenchPanelIds.SUPER_CHAT),
                        Tabs(0.35, WorkbenchPanelIds.GIFT),
                        Tabs(0.25, WorkbenchPanelIds.GUARD)
                    ]
                }
            ]
        };
    }

    private static WorkspaceLayoutNode Tabs(double proportion, string panelId)
    {
        return new()
        {
            Kind = WorkspaceLayoutNodeKind.Tabs,
            Proportion = proportion,
            PanelIds = [panelId],
            ActivePanelId = panelId
        };
    }
}
