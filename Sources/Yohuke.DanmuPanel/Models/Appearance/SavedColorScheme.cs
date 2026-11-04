namespace Yohuke.DanmuPanel.Models.Appearance;

/// <summary>包含基础主题和独立颜色快照的已保存方案。</summary>
public sealed class SavedColorScheme
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Theme { get; set; } = "system";
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);
}
