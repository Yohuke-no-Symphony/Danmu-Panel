using System.Globalization;
using System.Text;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>使用本地词库生成带声调拼音，保留非汉字片段。</summary>
public sealed class PinyinService : IPinyinService
{
    private readonly global::Pinyin.Pinyin converter = new();
    private readonly object gate = new();
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<PinyinAnnotation>> annotationCache = new(StringComparer.Ordinal);
    private const int MAX_CACHE_SIZE = 2000;

    /// <inheritdoc/>
    public IReadOnlyList<PinyinAnnotation> GetAnnotations(string text)
    {
        if (!text.Any(IsSupportedChinese))
        {
            return [];
        }

        lock (gate)
        {
            if (annotationCache.TryGetValue(text, out var cached))
            {
                return cached;
            }

            var readings = new Dictionary<int, string>();
            var sourceStart = 0;
            var converted = converter.HanziToPinyin(text, global::Pinyin.ManTone.Style.TONE,
                global::Pinyin.Error.Default, false, true, false);
            foreach (var segment in converted)
            {
                if (segment.hanzi.Length != 1 || !IsSupportedChinese(segment.hanzi[0]))
                {
                    continue;
                }

                sourceStart = text.IndexOf(segment.hanzi, sourceStart, StringComparison.Ordinal);
                if (!segment.error && !string.IsNullOrWhiteSpace(segment.pinyin) && segment.pinyin != segment.hanzi)
                {
                    readings.Add(sourceStart, segment.pinyin);
                }
                sourceStart += segment.hanzi.Length;
            }

            IReadOnlyList<PinyinAnnotation> result = [];
            if (readings.Count > 0)
            {
                var annotations = new List<PinyinAnnotation>();
                var elements = StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext())
                {
                    var start = elements.ElementIndex;
                    annotations.Add(new(start, elements.GetTextElement(), readings.GetValueOrDefault(start, "")));
                }
                result = annotations.AsReadOnly();
            }

            if (annotationCache.Count >= MAX_CACHE_SIZE)
            {
                annotationCache.Remove(annotationCache.Keys.First());
            }
            annotationCache[text] = result;
            return result;
        }
    }

    private static bool IsSupportedChinese(char character)
    {
        return character is >= '\u4E00' and <= '\u9FFF';
    }

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
