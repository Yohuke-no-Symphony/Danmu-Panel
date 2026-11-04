using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>保留消息原文，仅提取用于翻译的文字。</summary>
public static partial class TranslationTextExtractor
{
    /// <summary>移除协议表情和 Unicode 表情字素，纯表情返回空字符串。</summary>
    public static string Extract(string text, IEnumerable<string>? emoticonTexts = null)
    {
        var value = Whitespace().Replace(RemoveEmoticons(text, emoticonTexts).Normalize(NormalizationForm.FormC), " ").Trim();
        return value.EnumerateRunes().Any(Rune.IsLetterOrDigit) ? value : "";
    }

    /// <summary>判断正文是否仅包含协议表情、Unicode 表情及空白，不将标点或空消息视为表情。</summary>
    public static bool IsEmoticonOnly(string text, IEnumerable<string>? emoticonTexts = null)
    {
        return !string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(RemoveEmoticons(text, emoticonTexts));
    }

    private static string RemoveEmoticons(string text, IEnumerable<string>? emoticonTexts)
    {
        foreach (var token in (emoticonTexts ?? []).Where(token => !string.IsNullOrEmpty(token)).OrderByDescending(token => token.Length))
        {
            text = text.Replace(token, " ", StringComparison.Ordinal);
        }

        var result = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            result.Append(IsEmoji(element) ? " " : element);
        }

        return result.ToString();
    }

    private static bool IsEmoji(string element)
    {
        return element.EnumerateRunes().Any(rune => rune.Value is
            >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or
            >= 0x2194 and <= 0x21FF or >= 0x2300 and <= 0x23FF or
            >= 0x25AA and <= 0x25FF or >= 0x2B00 and <= 0x2BFF or
            0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
            0x20E3 or 0x3030 or 0x303D or 0x3297 or 0x3299);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
