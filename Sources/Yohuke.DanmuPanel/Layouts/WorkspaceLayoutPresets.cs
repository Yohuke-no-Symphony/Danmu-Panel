using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkspaceLayoutPresets
{
    internal const string DEFAULT = "default";

    internal static IReadOnlyList<WorkspaceLayoutPreset> BuiltIn =>
    [
        new() { Id = DEFAULT, Name = "Default", Layout = Create(DEFAULT) }
    ];

    internal static WorkspaceLayoutFile CreateDefaultFile()
    {
        return new() { CurrentPresetId = DEFAULT, CurrentLayout = Create(DEFAULT) };
    }

    internal static string GetDisplayName(string id, string customName)
    {
        return id == DEFAULT ? Localization.Get("Layout.Preset.Default") : customName;
    }

    internal static WorkspaceLayoutSnapshot Create(string presetId)
    {
        if (presetId != DEFAULT)
        {
            throw new ArgumentException(Localization.Get("Layout.UnknownBuiltIn"), nameof(presetId));
        }

        return new()
        {
            Main = new()
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
                            new()
                            {
                                Kind = WorkspaceLayoutNodeKind.Split,
                                Orientation = WorkspaceLayoutOrientation.Horizontal,
                                Proportion = 0.35,
                                Children =
                                [
                                    Tabs(0.5, WorkbenchPanelIds.GIFT),
                                    Tabs(0.5, WorkbenchPanelIds.RED_POCKET)
                                ]
                            },
                            Tabs(0.25, WorkbenchPanelIds.GUARD)
                        ]
                    }
                ]
            },
            FocusedPanelId = WorkbenchPanelIds.DANMU,
            HiddenPanelIds = [WorkbenchPanelIds.TEST_MESSAGES, WorkbenchPanelIds.LOG]
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
