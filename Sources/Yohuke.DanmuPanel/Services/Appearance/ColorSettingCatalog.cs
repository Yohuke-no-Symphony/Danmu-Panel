using System.Collections.Frozen;
using Yohuke.DanmuPanel.Models.Appearance;

namespace Yohuke.DanmuPanel.Services.Appearance;

/// <summary>集中描述界面与消息中允许覆盖的颜色。</summary>
public static class ColorSettingCatalog
{
    public const string SPECIAL_ATTENTION_USER_NAME_BRUSH = ColorSchemeSettings.SPECIAL_ATTENTION_USER_NAME_BRUSH;
    public const string SPECIAL_ATTENTION_CONTENT_BRUSH = ColorSchemeSettings.SPECIAL_ATTENTION_CONTENT_BRUSH;
    public const string GUARD_CAPTAIN_USER_NAME_BRUSH = "GuardCaptainUserNameBrush";
    public const string GUARD_ADMIRAL_USER_NAME_BRUSH = "GuardAdmiralUserNameBrush";
    public const string GUARD_GOVERNOR_USER_NAME_BRUSH = "GuardGovernorUserNameBrush";
    public const string ROOM_ADMIN_USER_NAME_BRUSH = "RoomAdminUserNameBrush";
    public const string GUARD_CAPTAIN_COLOR = "#4F8FDD";
    public const string GUARD_ADMIRAL_COLOR = "#A46EE8";
    public const string GUARD_GOVERNOR_COLOR = "#E9A23B";
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
        Define(SPECIAL_ATTENTION_USER_NAME_BRUSH, "Messages"),
        Define(SPECIAL_ATTENTION_CONTENT_BRUSH, "Messages"),
        Define("MessageUserNameBrush", "Messages", "AppTextBrush"),
        Define(GUARD_CAPTAIN_USER_NAME_BRUSH, "Messages"),
        Define(GUARD_ADMIRAL_USER_NAME_BRUSH, "Messages"),
        Define(GUARD_GOVERNOR_USER_NAME_BRUSH, "Messages"),
        Define(ROOM_ADMIN_USER_NAME_BRUSH, "Messages"),
        Define("MessagePinyinBrush", "Messages", "AppMutedTextBrush"),
        Define("MessageTimeBrush", "Messages", "AppMutedTextBrush"),
        Define("MedalBackgroundBrush", "Medals", "AppSidebarSelectedBrush"),
        Define("MedalCaptainBackgroundBrush", "Medals", color: GUARD_CAPTAIN_COLOR),
        Define("MedalAdmiralBackgroundBrush", "Medals", color: GUARD_ADMIRAL_COLOR),
        Define("MedalGovernorBackgroundBrush", "Medals", color: GUARD_GOVERNOR_COLOR),
        Define("MedalTextBrush", "Medals", "AppMutedTextBrush"),
        Define("GuardCaptainBrush", "Borders", color: GUARD_CAPTAIN_COLOR),
        Define("GuardAdmiralBrush", "Borders", color: GUARD_ADMIRAL_COLOR),
        Define("GuardGovernorBrush", "Borders", color: GUARD_GOVERNOR_COLOR),
        Define("SuperChatBorderBrush", "Borders"),
        Define("SuperChatValueUnder30Brush", "Borders", color: "#8B9BA8"),
        Define("SuperChatValueUnder50Brush", "Borders", color: "#3171D2"),
        Define("SuperChatValue50Brush", "Borders", color: "#427D9E"),
        Define("SuperChatValue100Brush", "Borders", color: "#E2B52B"),
        Define("SuperChatValue500Brush", "Borders", color: "#E09443"),
        Define("SuperChatValue1000Brush", "Borders", color: "#E54D4D"),
        Define("SuperChatValue2000Brush", "Borders", color: "#AB1A32"),
        Define("GiftValueUnder1Brush", "Borders", color: "#8B9BA8"),
        Define("GiftValue1Brush", "Borders", color: "#65B87A"),
        Define("GiftValue10Brush", "Borders", color: "#39B5C8"),
        Define("GiftValueUnder50Brush", "Borders", color: "#3171D2"),
        Define("GiftValue50Brush", "Borders", color: "#427D9E"),
        Define("GiftValue100Brush", "Borders", color: "#E2B52B"),
        Define("GiftValue500Brush", "Borders", color: "#E09443"),
        Define("GiftValue1000Brush", "Borders", color: "#E54D4D"),
        Define("GiftValue2000Brush", "Borders", color: "#AB1A32"),
        Define("PriorityMessageBorderBrush", "Borders", color: PRIORITY_MESSAGE_COLOR)
    ]);

    private static readonly FrozenDictionary<string, ColorSettingDefinition> definitionsByKey =
        Definitions.ToFrozenDictionary(definition => definition.Key, StringComparer.Ordinal);

    /// <summary>按稳定资源键取得颜色定义。</summary>
    public static ColorSettingDefinition Get(string key)
    {
        return definitionsByKey[key];
    }

    internal static ColorSettingDefinition GetGiftBorder(decimal totalPrice)
    {
        return Get(totalPrice switch
        {
            >= 2000 => "GiftValue2000Brush",
            >= 1000 => "GiftValue1000Brush",
            >= 500 => "GiftValue500Brush",
            >= 100 => "GiftValue100Brush",
            >= 50 => "GiftValue50Brush",
            >= 30 => "GiftValueUnder50Brush",
            >= 10 => "GiftValue10Brush",
            >= 1 => "GiftValue1Brush",
            _ => "GiftValueUnder1Brush"
        });
    }

    internal static bool Contains(string key)
    {
        return key == ColorSchemeSettings.LEGACY_SPECIAL_ATTENTION_MESSAGE_BRUSH || definitionsByKey.ContainsKey(key);
    }

    internal static ColorSettingDefinition GetSuperChatBorder(decimal price)
    {
        return Get(price switch
        {
            >= 2000 => "SuperChatValue2000Brush",
            >= 1000 => "SuperChatValue1000Brush",
            >= 500 => "SuperChatValue500Brush",
            >= 100 => "SuperChatValue100Brush",
            >= 50 => "SuperChatValue50Brush",
            >= 30 => "SuperChatValueUnder50Brush",
            _ => "SuperChatValueUnder30Brush"
        });
    }

    private static ColorSettingDefinition Define(string key, string group, string? resource = null, string? color = null)
    {
        return new(key, "Colors." + key, "Colors." + group) { DefaultResourceKey = resource, DefaultColor = color };
    }
}
