namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>独立于界面语言保存的自动翻译设置。</summary>
public sealed class TranslationSettings
{
    public const int DEFAULT_CACHE_CAPACITY = 5000;
    public const int MAX_CACHE_CAPACITY = 10000;
    public const int DEFAULT_CACHE_TTL_MINUTES = 1440;
    public const int MIN_CACHE_TTL_MINUTES = 1;
    public const int MAX_CACHE_TTL_MINUTES = 43200;

    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string ExtraSystemPrompt { get; set; } = "";
    public List<GlossaryEntry> Glossary { get; set; } = [];
    /// <summary>本机的目标语言指纹映射，用于识别旧版保存的译文和待办。</summary>
    public Dictionary<string, string> CacheFingerprints { get; set; } = new(StringComparer.Ordinal);
    public TranslationDisplayMode DisplayMode { get; set; } = TranslationDisplayMode.Bilingual;
    public TranslationBacklogMode BacklogMode { get; set; } = TranslationBacklogMode.Realtime;

    /// <summary>可复用译文的最大条数。设为零时停用结果缓存。</summary>
    public int CacheCapacity { get; set; } = DEFAULT_CACHE_CAPACITY;

    /// <summary>译文自成功写入起可复用的分钟数。</summary>
    public int CacheTtlMinutes { get; set; } = DEFAULT_CACHE_TTL_MINUTES;
}
