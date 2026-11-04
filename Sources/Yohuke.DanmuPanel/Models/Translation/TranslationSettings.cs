namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>独立于界面语言保存的自动翻译设置。</summary>
public sealed class TranslationSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string ExtraSystemPrompt { get; set; } = "";
    public List<GlossaryEntry> Glossary { get; set; } = [];
    public TranslationDisplayMode DisplayMode { get; set; } = TranslationDisplayMode.Bilingual;
    public TranslationBacklogMode BacklogMode { get; set; } = TranslationBacklogMode.Realtime;
}
