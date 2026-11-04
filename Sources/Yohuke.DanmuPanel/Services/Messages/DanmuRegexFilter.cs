using System.Text.RegularExpressions;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services.Messages;

/// <summary>复用独立的正则快照，在有限匹配时间内判断普通弹幕是否应隐藏。</summary>
public sealed class DanmuRegexFilter
{
    private const int MATCH_TIMEOUT_MS = 25;
    private readonly Regex[] patterns;
    private readonly int[] timedOutPatterns;

    public bool Enabled { get; }

    /// <summary>报告本快照内首次超时并暂停的规则序号，序号从一开始。</summary>
    public event Action<int>? PatternTimedOut;

    /// <summary>校验并创建与可编辑设置完全独立的匹配快照。</summary>
    public DanmuRegexFilter(DanmuRegexFilterSettings settings)
    {
        patterns = CompilePatterns(settings);
        timedOutPatterns = new int[patterns.Length];
        Enabled = settings.Enabled;
    }

    /// <summary>任意规则命中即隐藏；超时规则暂停至下次保存设置，并继续检查其他规则。</summary>
    public bool IsBlocked(string content)
    {
        if (!Enabled)
        {
            return false;
        }
        for (var index = 0; index < patterns.Length; index++)
        {
            if (Volatile.Read(ref timedOutPatterns[index]) != 0)
            {
                continue;
            }
            try
            {
                if (patterns[index].IsMatch(content))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                if (Interlocked.Exchange(ref timedOutPatterns[index], 1) == 0)
                {
                    PatternTimedOut?.Invoke(index + 1);
                }
            }
        }
        return false;
    }

    /// <summary>验证可持久化的规则结构及语法，无效配置以数据错误报告。</summary>
    public static void Validate(DanmuRegexFilterSettings settings)
    {
        _ = CompilePatterns(settings);
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

    private static Regex[] CompilePatterns(DanmuRegexFilterSettings settings)
    {
        if (settings is null || settings.Patterns is null)
        {
            throw new InvalidDataException("The danmu regex filter settings are missing.");
        }
        var result = new List<Regex>(settings.Patterns.Count);
        for (var index = 0; index < settings.Patterns.Count; index++)
        {
            var pattern = settings.Patterns[index];
            if (!IsSingleLinePattern(pattern))
            {
                throw new InvalidDataException($"Danmu regex pattern {index + 1} must be a nonempty single line.");
            }
            try
            {
                result.Add(CreatePattern(pattern));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"Danmu regex pattern {index + 1} is invalid.", exception);
            }
        }
        return [.. result];
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
