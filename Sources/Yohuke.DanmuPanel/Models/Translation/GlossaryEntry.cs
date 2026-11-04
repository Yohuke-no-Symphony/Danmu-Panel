namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>针对指定目标语言的术语译法。</summary>
public sealed class GlossaryEntry
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
}
