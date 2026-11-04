namespace Yohuke.DanmuPanel.Models.Appearance;

/// <summary>当前颜色覆盖与可重复应用的个人配色方案。</summary>
public sealed class ColorSchemeSettings
{
    public Dictionary<string, string> Overrides { get; set; } = new(StringComparer.Ordinal);
    public List<SavedColorScheme> SavedSchemes { get; set; } = [];
    public string? SelectedSchemeId { get; set; }
}
