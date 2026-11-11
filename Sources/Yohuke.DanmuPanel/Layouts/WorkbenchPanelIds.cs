namespace Yohuke.DanmuPanel.Layouts;

internal static class WorkbenchPanelIds
{
    internal const string WORKSPACE = "workspace";
    internal const string DANMU = "danmu";
    internal const string SUPER_CHAT = "superChat";
    internal const string GIFT = "gift";
    internal const string GUARD = "guard";
    internal const string TEST_MESSAGES = "testMessages";
    internal const string LOG = "log";
    internal const string RED_POCKET = "redPocket";

    private static IReadOnlyList<string> VersionOne { get; } = Array.AsReadOnly(new[]
    {
        WORKSPACE, DANMU, SUPER_CHAT, GIFT, GUARD
    });

    private static IReadOnlyList<string> VersionTwo { get; } = Array.AsReadOnly(VersionOne.Concat([TEST_MESSAGES]).ToArray());

    private static IReadOnlyList<string> VersionThree { get; } = Array.AsReadOnly(VersionTwo.Concat([LOG]).ToArray());

    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(VersionThree.Concat([RED_POCKET]).ToArray());

    internal static IReadOnlyList<string> ForVersion(int version)
    {
        return version switch
        {
            1 => VersionOne,
            2 => VersionTwo,
            3 => VersionThree,
            4 => All,
            _ => []
        };
    }
}
