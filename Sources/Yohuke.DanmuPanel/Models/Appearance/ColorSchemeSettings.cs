using System.Text.Json.Serialization;

namespace Yohuke.DanmuPanel.Models.Appearance;

/// <summary>当前颜色覆盖与可重复应用的个人配色方案。</summary>
public sealed class ColorSchemeSettings : IJsonOnDeserialized
{
    internal const string LEGACY_SPECIAL_ATTENTION_MESSAGE_BRUSH = "SpecialAttentionMessageBrush";
    internal const string SPECIAL_ATTENTION_USER_NAME_BRUSH = "SpecialAttentionUserNameBrush";
    internal const string SPECIAL_ATTENTION_CONTENT_BRUSH = "SpecialAttentionContentBrush";

    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);
    public List<SavedColorScheme> SavedSchemes { get; set; } = [];
    public string? SelectedSchemeId { get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        UpgradeLegacyAttentionColors();
    }

    internal void UpgradeLegacyAttentionColors()
    {
        if (Overrides is not null)
        {
            UpgradeLegacyAttentionColors(Overrides);
        }
        if (SavedSchemes is not null)
        {
            foreach (var scheme in SavedSchemes)
            {
                if (scheme?.Colors is { } colors)
                {
                    UpgradeLegacyAttentionColors(colors);
                }
            }
        }
    }

    private static void UpgradeLegacyAttentionColors(Dictionary<string, string> colors)
    {
        if (!colors.Remove(LEGACY_SPECIAL_ATTENTION_MESSAGE_BRUSH, out var legacyColor))
        {
            return;
        }
        colors.TryAdd(SPECIAL_ATTENTION_USER_NAME_BRUSH, legacyColor);
        colors.TryAdd(SPECIAL_ATTENTION_CONTENT_BRUSH, legacyColor);
    }
}
