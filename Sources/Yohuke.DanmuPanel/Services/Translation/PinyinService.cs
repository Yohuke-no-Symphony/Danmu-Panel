using System.Text;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>使用本地词库生成带声调拼音，保留非汉字片段。</summary>
public sealed class PinyinService : IPinyinService
{
    private readonly global::Pinyin.Pinyin converter = new();
    private readonly object gate = new();
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private const int MAX_CACHE_SIZE = 2000;

    /// <summary>返回名称的带声调拼音；不含可转换汉字时返回空字符串。</summary>
    public string GetPinyin(string text)
    {
        if (!text.Any(character => character is >= '\u4E00' and <= '\u9FFF') ||
            text.Any(character => character is >= '\u3040' and <= '\u30FF' or >= '\uFF66' and <= '\uFF9F'))
        {
            return "";
        }

        lock (gate)
        {
            if (cache.TryGetValue(text, out var value))
            {
                return value;
            }

            var converted = converter.HanziToPinyin(text, global::Pinyin.ManTone.Style.TONE,
                global::Pinyin.Error.Default, false, true, false);
            var builder = new StringBuilder();
            var previousWasChinese = false;
            foreach (var segment in converted)
            {
                var isChinese = segment.hanzi.Any(character => character is >= '\u4E00' and <= '\u9FFF');
                if (builder.Length > 0 && (isChinese || previousWasChinese) && !char.IsWhiteSpace(builder[^1]))
                {
                    builder.Append(' ');
                }

                builder.Append(isChinese ? segment.pinyin : segment.hanzi);
                previousWasChinese = isChinese;
            }

            var result = builder.ToString();
            if (cache.Count >= MAX_CACHE_SIZE)
            {
                cache.Remove(cache.Keys.First());
            }

            cache[text] = result;
            return result;
        }
    }
}
