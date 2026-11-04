namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>可直接展示且不包含凭据或请求正文的翻译错误。</summary>
public sealed class TranslationException(string message) : Exception(message)
{
}
