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
    /// <summary>本机旧版缓存的语言身份映射，不随翻译服务配置变化或设置分享迁移。</summary>
    public Dictionary<string, string> CacheFingerprints { get; set; } = new(StringComparer.Ordinal);
    public TranslationDisplayMode DisplayMode { get; set; } = TranslationDisplayMode.Bilingual;
    public TranslationBacklogMode BacklogMode { get; set; } = TranslationBacklogMode.Realtime;
}
