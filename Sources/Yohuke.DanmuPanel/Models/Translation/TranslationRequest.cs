namespace Yohuke.DanmuPanel.Models.Translation;

/// <summary>一次不可变的翻译请求快照。</summary>
public sealed record TranslationRequest
{
    public string CacheKey { get; init; } = "";
    public string Text { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public string ConfigurationFingerprint { get; init; } = "";
}
