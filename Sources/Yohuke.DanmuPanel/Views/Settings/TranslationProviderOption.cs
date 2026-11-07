namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>翻译服务的名称和预设 API 地址。</summary>
public sealed record TranslationProviderOption(string Name, string? BaseUrl)
{
    public bool IsCustom => BaseUrl is null;
    public bool IsPreset => BaseUrl is not null;

    internal static IReadOnlyList<TranslationProviderOption> Presets { get; } =
    [
        new("OpenAI", "https://api.openai.com/v1"),
        new("DeepSeek", "https://api.deepseek.com"),
        new("Anthropic (Claude)", "https://api.anthropic.com/v1"),
        new("Custom", null)
    ];
}
