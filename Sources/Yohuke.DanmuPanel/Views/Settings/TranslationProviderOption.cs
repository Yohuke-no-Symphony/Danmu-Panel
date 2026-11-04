namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed record TranslationProviderOption(string Name, string? BaseUrl)
{
    internal static IReadOnlyList<TranslationProviderOption> Presets { get; } =
    [
        new("OpenAI", "https://api.openai.com/v1"),
        new("DeepSeek", "https://api.deepseek.com"),
        new("Anthropic (Claude)", "https://api.anthropic.com/v1"),
        new("Custom", null)
    ];
}
