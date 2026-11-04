using System.Collections.Frozen;

namespace Yohuke.DanmuPanel.Services.Appearance;

/// <summary>集中描述界面与消息中允许覆盖的颜色。</summary>
public static class ColorSettingCatalog
{
    public const string GUARD_CAPTAIN_COLOR = "#4F8FDD";
    public const string GUARD_ADMIRAL_COLOR = "#A46EE8";
    public const string GUARD_GOVERNOR_COLOR = "#E9A23B";
    public const string SUPER_CHAT_FALLBACK_COLOR = "#2A60B2";
    public const string PRIORITY_MESSAGE_COLOR = "#5B9BD5";

    public static IReadOnlyList<ColorSettingDefinition> Definitions { get; } = Array.AsReadOnly<ColorSettingDefinition>(
    [
        Define("AppBackgroundBrush", "Interface"),
        Define("AppTitleBarBrush", "Interface"),
        Define("AppSidebarBrush", "Interface"),
        Define("AppPanelBrush", "Interface"),
        Define("AppCardBrush", "Interface"),
        Define("AppBorderBrush", "Interface"),
        Define("AppTextBrush", "Interface"),
        Define("AppMutedTextBrush", "Interface"),
        Define("AppPrimaryBrush", "Interface"),
        Define("AppPrimaryTextBrush", "Interface"),
        Define("AppSplitterBrush", "Interface"),
        Define("AppOverlayBrush", "Interface"),
        Define("AppSidebarSelectedBrush", "Interface"),
        Define("AppSidebarHoverBrush", "Interface"),
        Define("AppSettingsFooterBrush", "Interface"),
        Define("AppAvatarBrush", "Interface"),
        Define("AppAvatarTextBrush", "Interface"),
        Define("AppErrorBrush", "Interface"),
        Define("AppSuccessBrush", "Interface"),
        Define("AppWarningBrush", "Interface"),
        Define("AppDanmuAlternateRowBrush", "Messages"),
        Define("MessageOriginalBrush", "Messages", "AppTextBrush"),
        Define("MessageTranslationBrush", "Messages", "AppTextBrush"),
        Define("MessageUserNameBrush", "Messages", "AppTextBrush"),
        Define("MessagePinyinBrush", "Messages", "AppMutedTextBrush"),
        Define("MessageTimeBrush", "Messages", "AppMutedTextBrush"),
        Define("MedalBackgroundBrush", "Medals", "AppSidebarSelectedBrush"),
        Define("MedalTextBrush", "Medals", "AppMutedTextBrush"),
        Define("GuardCaptainBrush", "Borders", color: GUARD_CAPTAIN_COLOR),
        Define("GuardAdmiralBrush", "Borders", color: GUARD_ADMIRAL_COLOR),
        Define("GuardGovernorBrush", "Borders", color: GUARD_GOVERNOR_COLOR),
        Define("SuperChatBorderBrush", "Borders"),
        Define("PriorityMessageBorderBrush", "Borders", color: PRIORITY_MESSAGE_COLOR)
    ]);

    private static readonly FrozenDictionary<string, ColorSettingDefinition> definitionsByKey =
        Definitions.ToFrozenDictionary(definition => definition.Key, StringComparer.Ordinal);

    /// <summary>按稳定资源键取得颜色定义。</summary>
    public static ColorSettingDefinition Get(string key)
    {
        return definitionsByKey[key];
    }

    internal static bool Contains(string key)
    {
        return definitionsByKey.ContainsKey(key);
    }

    private static ColorSettingDefinition Define(string key, string group, string? resource = null, string? color = null)
    {
        return new(key, "Colors." + key, "Colors." + group) { DefaultResourceKey = resource, DefaultColor = color };
    }
}
