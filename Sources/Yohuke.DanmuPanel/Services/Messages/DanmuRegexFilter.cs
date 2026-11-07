using System.Text.RegularExpressions;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

/// <summary>复用独立的正则快照，在有限匹配时间内判断普通弹幕的显示及翻译屏蔽。</summary>
public sealed class DanmuRegexFilter
{
    private const int MATCH_TIMEOUT_MS = 25;
    private readonly DanmuRegexRuleSnapshot[] rules;
    private readonly int[] timedOutPatterns;
    private readonly bool blocksTranslation;
    private int stateVersion;

    public bool Enabled { get; }
    internal int StateVersion => Volatile.Read(ref stateVersion);

    /// <summary>报告本快照内首次超时并暂停的规则序号，序号从一开始。</summary>
    public event Action<int>? PatternTimedOut;

    /// <summary>校验并创建与可编辑设置完全独立的匹配快照。</summary>
    public DanmuRegexFilter(DanmuRegexFilterSettings settings)
    {
        rules = CompileRules(settings);
        timedOutPatterns = new int[rules.Length];
        Enabled = rules.Any(rule => rule.BlockDisplay);
        blocksTranslation = rules.Any(rule => rule.BlocksTranslation);
    }

    /// <summary>任意规则命中即隐藏；超时规则暂停至下次保存设置，并继续检查其他规则。</summary>
    public bool IsBlocked(string content)
    {
        return Enabled && Matches(content, false);
    }

    /// <summary>命中任意完整屏蔽或翻译屏蔽规则时，普通弹幕均不参与翻译。</summary>
    public bool IsTranslationBlocked(string content)
    {
        return blocksTranslation && Matches(content, true);
    }

    internal bool HasSameSettings(DanmuRegexFilterSettings settings)
    {
        if (settings.Rules is null || rules.Length != settings.Rules.Count)
        {
            return false;
        }
        for (var index = 0; index < rules.Length; index++)
        {
            var rule = settings.Rules[index];
            if (rule is null || rules[index].Pattern.ToString() != rule.Pattern ||
                rules[index].BlockDisplay != rule.BlockDisplay || rules[index].BlockTranslation != rule.BlockTranslation)
            {
                return false;
            }
        }
        return true;
    }

    internal bool HasSameTranslationRules(DanmuRegexFilter other)
    {
        if (blocksTranslation != other.blocksTranslation)
        {
            return false;
        }
        if (!blocksTranslation)
        {
            return true;
        }
        var otherIndex = 0;
        for (var index = 0; index < rules.Length; index++)
        {
            if (!rules[index].BlocksTranslation)
            {
                continue;
            }
            while (otherIndex < other.rules.Length && !other.rules[otherIndex].BlocksTranslation)
            {
                otherIndex++;
            }
            if (otherIndex == other.rules.Length || rules[index].Pattern.ToString() != other.rules[otherIndex].Pattern.ToString() ||
                Volatile.Read(ref timedOutPatterns[index]) != Volatile.Read(ref other.timedOutPatterns[otherIndex]))
            {
                return false;
            }
            otherIndex++;
        }
        while (otherIndex < other.rules.Length && !other.rules[otherIndex].BlocksTranslation)
        {
            otherIndex++;
        }
        return otherIndex == other.rules.Length;
    }

    /// <summary>验证可持久化的规则结构及语法，无效配置以数据错误报告。</summary>
    public static void Validate(DanmuRegexFilterSettings settings)
    {
        _ = CompileRules(settings);
    }

    /// <summary>检查单行表达式是否合法，供设置草稿定位无效规则。</summary>
    public static bool IsValidPattern(string pattern)
    {
        if (!IsSingleLinePattern(pattern))
        {
            return false;
        }
        try
        {
            _ = CreatePattern(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool Matches(string content, bool forTranslation)
    {
        for (var index = 0; index < rules.Length; index++)
        {
            var enabled = forTranslation ? rules[index].BlocksTranslation : rules[index].BlockDisplay;
            if (!enabled || Volatile.Read(ref timedOutPatterns[index]) != 0)
            {
                continue;
            }
            try
            {
                if (rules[index].Pattern.IsMatch(content))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                if (Interlocked.Exchange(ref timedOutPatterns[index], 1) == 0)
                {
                    Interlocked.Increment(ref stateVersion);
                    PatternTimedOut?.Invoke(index + 1);
                }
            }
        }
        return false;
    }

    private static DanmuRegexRuleSnapshot[] CompileRules(DanmuRegexFilterSettings settings)
    {
        if (settings is null || settings.Rules is null)
        {
            throw new InvalidDataException("The danmu regex filter settings are missing.");
        }
        var result = new DanmuRegexRuleSnapshot[settings.Rules.Count];
        for (var index = 0; index < settings.Rules.Count; index++)
        {
            var rule = settings.Rules[index];
            if (rule is null || !IsSingleLinePattern(rule.Pattern))
            {
                throw new InvalidDataException($"Danmu regex pattern {index + 1} must be a nonempty single line.");
            }
            try
            {
                result[index] = new(CreatePattern(rule.Pattern), rule.BlockDisplay, rule.BlockTranslation);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Danmu regex pattern {index + 1} is invalid.", exception);
            }
        }
        return result;
    }

    private static bool IsSingleLinePattern(string pattern)
    {
        return !string.IsNullOrWhiteSpace(pattern) && pattern.IndexOfAny(['\r', '\n']) < 0;
    }

    private static Regex CreatePattern(string pattern)
    {
        return new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(MATCH_TIMEOUT_MS));
    }
}
