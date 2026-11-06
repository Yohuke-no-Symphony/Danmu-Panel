using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证同传默认规则、自定义规则和匹配超时隔离。</summary>
public sealed class DanmuRegexFilterTests
{
    /// <summary>默认规则允许任意联动人名前缀，但闭括号之后不能再有正文。</summary>
    [Theory]
    [InlineData("【内容】", true)]
    [InlineData("【】", true)]
    [InlineData("菜【内容】", true)]
    [InlineData("其他主播【翻译内容*】", true)]
    [InlineData("AB【内容】", true)]
    [InlineData("我喜欢【这首歌】的歌词", false)]
    [InlineData("菜【内容】 后续文字", false)]
    [InlineData("菜【内容】 ", false)]
    [InlineData("菜【未完成", false)]
    [InlineData("普通弹幕", false)]
    public void DefaultRuleMatchesOnlyTranslationFormat(string content, bool blocked)
    {
        var filter = new DanmuRegexFilter(new() { Enabled = true });

        Assert.Equal(blocked, filter.IsBlocked(content));
    }

    /// <summary>多条规则取任一命中，关闭后规则保留但不再屏蔽。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MultipleRulesRespectGlobalSwitch(bool enabled)
    {
        var settings = new DanmuRegexFilterSettings { Enabled = enabled, Patterns = ["^广告", "机器人$"] };
        var filter = new DanmuRegexFilter(settings);

        Assert.Equal(enabled, filter.IsBlocked("广告内容"));
        Assert.Equal(enabled, filter.IsBlocked("我是机器人"));
        Assert.False(filter.IsBlocked("正常聊天"));
        Assert.Equal(2, settings.Patterns.Count);
    }

    /// <summary>空列表不自动恢复默认规则，匹配快照不受后续列表修改影响。</summary>
    [Fact]
    public void SnapshotOwnsItsRulesAndEmptyListBlocksNothing()
    {
        var settings = new DanmuRegexFilterSettings { Enabled = true, Patterns = ["^广告"] };
        var filter = new DanmuRegexFilter(settings);
        settings.Patterns.Clear();
        settings.Enabled = false;

        Assert.True(filter.IsBlocked("广告内容"));
        Assert.False(new DanmuRegexFilter(new() { Enabled = true, Patterns = [] }).IsBlocked("【内容】"));
    }

    /// <summary>无效规则在创建匹配器前拒绝，关闭状态同样不能保存损坏规则。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidPatternsAreRejected(bool enabled)
    {
        var settings = new DanmuRegexFilterSettings { Enabled = enabled, Patterns = ["^广告", "["] };

        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(settings));
        Assert.Throws<InvalidDataException>(() => DanmuRegexFilter.Validate(settings));
        Assert.False(DanmuRegexFilter.IsValidPattern("["));
    }

    /// <summary>病态表达式匹配超时后放行，并继续尝试其他有效规则。</summary>
    [Fact]
    public void TimedOutPatternDoesNotPreventOtherRulesFromMatching()
    {
        var content = new string('a', 100_000) + "!";
        var filter = new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^(a+)+$"] });
        var combined = new DanmuRegexFilter(new() { Enabled = true, Patterns = ["^(a+)+$", "!$"] });

        Assert.False(filter.IsBlocked(content));
        Assert.True(combined.IsBlocked(content));
    }

    /// <summary>超时只报告一次，后续不同内容跳过该规则，重新创建快照后才重试。</summary>
    [Fact]
    public void TimedOutPatternIsSuspendedForTheSettingsSnapshot()
    {
        var settings = new DanmuRegexFilterSettings { Enabled = true, Patterns = ["^(a+)+$", "^广告"] };
        var filter = new DanmuRegexFilter(settings);
        var timedOut = new List<int>();
        filter.PatternTimedOut += timedOut.Add;

        Assert.False(filter.IsBlocked(new string('a', 100_000) + "!"));
        Assert.False(filter.IsBlocked(new string('a', 100_001) + "!"));
        Assert.False(filter.IsBlocked("aaa"));
        Assert.True(filter.IsBlocked("广告内容"));
        Assert.Equal(1, Assert.Single(timedOut));
        Assert.True(new DanmuRegexFilter(settings).IsBlocked("aaa"));
    }

    /// <summary>每行一条的规则契约拒绝真实换行，转义换行仍可用于匹配。</summary>
    [Theory]
    [InlineData("^a\nb$")]
    [InlineData("^a\rb$")]
    public void LiteralLineBreaksCannotChangeMeaningAfterEditing(string pattern)
    {
        Assert.False(DanmuRegexFilter.IsValidPattern(pattern));
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(new() { Patterns = [pattern] }));
        Assert.True(new DanmuRegexFilter(new() { Enabled = true, Patterns = [@"^a\nb$"] }).IsBlocked("a\nb"));
    }
}
