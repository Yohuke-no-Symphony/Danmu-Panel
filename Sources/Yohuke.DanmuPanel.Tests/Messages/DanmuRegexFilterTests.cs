using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Messages;

/// <summary>验证逐条屏蔽规则、独立快照、选择性比较和匹配超时隔离。</summary>
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
        var filter = new DanmuRegexFilter(DefaultSettings(true));

        Assert.Equal(blocked, filter.IsBlocked(content));
    }

    /// <summary>音乐符号出现在任意位置均命中，其他表情不会被代理项误匹配。</summary>
    [Theory]
    [InlineData("♩歌词", true)]
    [InlineData("歌词♪", true)]
    [InlineData("前奏♫结束", true)]
    [InlineData("♬", true)]
    [InlineData("♭", true)]
    [InlineData("♮", true)]
    [InlineData("♯", true)]
    [InlineData("🎵歌词", true)]
    [InlineData("歌词🎶", true)]
    [InlineData("前奏🎵结束", true)]
    [InlineData("普通弹幕", false)]
    [InlineData("😀", false)]
    [InlineData("🍜", false)]
    [InlineData("🇯🇵", false)]
    public void DefaultMusicRuleMatchesCompleteSymbols(string content, bool blocked)
    {
        var filter = new DanmuRegexFilter(DefaultSettings(true));

        Assert.Equal(blocked, filter.IsBlocked(content));
        Assert.Equal(blocked, filter.IsTranslationBlocked(content));
    }

    /// <summary>两个开关独立生效，完整屏蔽同时阻止显示和翻译。</summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public void DisplayAndTranslationBlockingRespectIndependentSwitches(bool enabled, bool blockTranslation,
        bool displayBlocked, bool translationBlocked)
    {
        var filter = new DanmuRegexFilter(new()
        {
            Rules = [new() { Pattern = "^广告", BlockDisplay = enabled, BlockTranslation = blockTranslation }]
        });

        Assert.Equal(displayBlocked, filter.IsBlocked("广告内容"));
        Assert.Equal(translationBlocked, filter.IsTranslationBlocked("广告内容"));
        Assert.False(filter.IsBlocked("正常聊天"));
        Assert.False(filter.IsTranslationBlocked("正常聊天"));
    }

    /// <summary>多条规则各自开启，命中任意一条即屏蔽，关闭后规则文本仍保留。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MultipleRulesRespectTheirOwnSwitches(bool enabled)
    {
        var settings = new DanmuRegexFilterSettings
        {
            Rules = [new() { Pattern = "^广告", BlockDisplay = enabled }, new() { Pattern = "机器人$", BlockDisplay = enabled }]
        };
        var filter = new DanmuRegexFilter(settings);

        Assert.Equal(enabled, filter.IsBlocked("广告内容"));
        Assert.Equal(enabled, filter.IsBlocked("我是机器人"));
        Assert.False(filter.IsBlocked("正常聊天"));
        Assert.Equal(2, settings.Rules.Count);
    }

    /// <summary>空列表不自动恢复默认规则，匹配快照不受后续列表修改影响。</summary>
    [Fact]
    public void SnapshotOwnsItsRulesAndEmptyListBlocksNothing()
    {
        var settings = new DanmuRegexFilterSettings { Rules = [new() { Pattern = "^广告", BlockDisplay = true }] };
        var filter = new DanmuRegexFilter(settings);
        settings.Rules[0].Pattern = "^普通";
        settings.Rules[0].BlockDisplay = false;
        settings.Rules[0].BlockTranslation = true;
        settings.Rules.Clear();

        Assert.True(filter.IsBlocked("广告内容"));
        var empty = new DanmuRegexFilter(new() { Rules = [] });
        Assert.False(empty.Enabled);
        Assert.False(empty.IsBlocked("【内容】♪🎵"));
        Assert.False(empty.IsTranslationBlocked("【内容】♪🎵"));
        Assert.False(filter.IsBlocked("♪🎵"));
        Assert.False(filter.IsBlocked("普通弹幕"));
        Assert.False(filter.IsTranslationBlocked("♪🎵"));
    }

    /// <summary>翻译屏蔽快照不受可编辑开关和规则集合的后续变化影响。</summary>
    [Fact]
    public void SnapshotOwnsIndependentTranslationSwitch()
    {
        var settings = new DanmuRegexFilterSettings { Rules = [new() { Pattern = "^广告", BlockTranslation = true }] };
        var filter = new DanmuRegexFilter(settings);
        settings.Rules[0].BlockTranslation = false;
        settings.Rules[0].BlockDisplay = true;
        settings.Rules.Clear();

        Assert.False(filter.Enabled);
        Assert.False(filter.IsBlocked("广告内容"));
        Assert.True(filter.IsTranslationBlocked("广告内容"));
    }

    /// <summary>无效规则在创建匹配器前拒绝，关闭状态同样不能保存损坏规则。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InvalidPatternsAreRejected(bool enabled)
    {
        var settings = new DanmuRegexFilterSettings
        {
            Rules = [new() { Pattern = "^广告", BlockDisplay = enabled }, new() { Pattern = "[", BlockDisplay = enabled }]
        };

        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(settings));
        Assert.Throws<InvalidDataException>(() => DanmuRegexFilter.Validate(settings));
        Assert.False(DanmuRegexFilter.IsValidPattern("["));
    }

    /// <summary>病态表达式匹配超时后放行，并继续尝试其他有效规则。</summary>
    [Fact]
    public void TimedOutPatternDoesNotPreventOtherRulesFromMatching()
    {
        var content = new string('a', 100_000) + "!";
        var filter = new DanmuRegexFilter(new() { Rules = [new() { Pattern = "^(a+)+$", BlockDisplay = true }] });
        var combined = new DanmuRegexFilter(new()
        {
            Rules = [new() { Pattern = "^(a+)+$", BlockDisplay = true }, new() { Pattern = "!$", BlockDisplay = true }]
        });

        Assert.False(filter.IsBlocked(content));
        Assert.True(combined.IsBlocked(content));
    }

    /// <summary>超时只报告一次，后续不同内容跳过该规则，重新创建快照后才重试。</summary>
    [Fact]
    public void TimedOutPatternIsSuspendedForTheSettingsSnapshot()
    {
        var settings = new DanmuRegexFilterSettings
        {
            Rules = [new() { Pattern = "^(a+)+$", BlockDisplay = true }, new() { Pattern = "^广告", BlockDisplay = true }]
        };
        var filter = new DanmuRegexFilter(settings);
        var timedOut = new List<int>();
        filter.PatternTimedOut += timedOut.Add;

        Assert.False(filter.IsTranslationBlocked(new string('a', 100_000) + "!"));
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
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(new() { Rules = [new() { Pattern = pattern }] }));
        Assert.True(new DanmuRegexFilter(new()
        {
            Rules = [new() { Pattern = @"^a\nb$", BlockDisplay = true }]
        }).IsBlocked("a\nb"));
    }

    /// <summary>翻译命中不会掩盖后面的完整屏蔽，禁用行不参与任一用途。</summary>
    [Fact]
    public void MixedAndOverlappingRulesPreserveFullBlocking()
    {
        var filter = new DanmuRegexFilter(new()
        {
            Rules =
            [
                new() { Pattern = "广告", BlockTranslation = true },
                new() { Pattern = "^广告", BlockDisplay = true },
                new() { Pattern = "机器人$", BlockTranslation = true },
                new() { Pattern = "普通" }
            ]
        });

        Assert.True(filter.Enabled);
        Assert.True(filter.IsBlocked("广告内容"));
        Assert.True(filter.IsTranslationBlocked("广告内容"));
        Assert.False(filter.IsBlocked("聊天里的广告"));
        Assert.True(filter.IsTranslationBlocked("聊天里的广告"));
        Assert.False(filter.IsBlocked("我是机器人"));
        Assert.True(filter.IsTranslationBlocked("我是机器人"));
        Assert.False(filter.IsBlocked("普通弹幕"));
        Assert.False(filter.IsTranslationBlocked("普通弹幕"));
    }

    /// <summary>禁用行的文本、数量及位置变化与等效屏蔽模式互切均不改变有效翻译规则。</summary>
    [Fact]
    public void TranslationComparisonIgnoresDisabledRowsAndEquivalentModes()
    {
        var original = new DanmuRegexFilter(new()
        {
            Rules =
            [
                new() { Pattern = "^广告", BlockDisplay = true },
                new() { Pattern = "旧禁用规则" },
                new() { Pattern = "机器人$", BlockTranslation = true }
            ]
        });
        var changed = new DanmuRegexFilterSettings
        {
            Rules =
            [
                new() { Pattern = "新禁用规则" },
                new() { Pattern = "^广告", BlockTranslation = true },
                new() { Pattern = "机器人$", BlockDisplay = true },
                new() { Pattern = "另一条禁用规则" }
            ]
        };

        Assert.True(original.HasSameTranslationRules(new(changed)));
        Assert.False(original.HasSameSettings(changed));
        Assert.True(new DanmuRegexFilter(new() { Rules = [new() { Pattern = "旧禁用" }] })
            .HasSameTranslationRules(new(new() { Rules = [new() { Pattern = "新禁用" }] })));
    }

    /// <summary>草稿比较包括全部行的顺序、文本及两个开关。</summary>
    [Fact]
    public void SettingsComparisonTracksEveryDraftValue()
    {
        var settings = new DanmuRegexFilterSettings
        {
            Rules = [new() { Pattern = "^广告", BlockDisplay = true }, new() { Pattern = "机器人$" }]
        };
        var filter = new DanmuRegexFilter(settings);
        Assert.True(filter.HasSameSettings(settings));

        settings.Rules[1].BlockTranslation = true;
        Assert.False(filter.HasSameSettings(settings));
        settings.Rules[1].BlockTranslation = false;
        settings.Rules[1].Pattern = "不同规则";
        Assert.False(filter.HasSameSettings(settings));
        settings.Rules[1].Pattern = "机器人$";
        settings.Rules.Reverse();
        Assert.False(filter.HasSameSettings(settings));
    }

    /// <summary>有效规则的文本、开关和先后顺序变化需要重新调度。</summary>
    [Fact]
    public void TranslationComparisonTracksEffectivePatternsAndOrder()
    {
        var settings = new DanmuRegexFilterSettings
        {
            Rules =
            [
                new() { Pattern = "^广告", BlockDisplay = true },
                new() { Pattern = "机器人$", BlockTranslation = true }
            ]
        };
        var original = new DanmuRegexFilter(settings);
        settings.Rules[0].Pattern = "^其他";
        Assert.False(original.HasSameTranslationRules(new(settings)));
        settings.Rules[0].Pattern = "^广告";
        settings.Rules[0].BlockDisplay = false;
        Assert.False(original.HasSameTranslationRules(new(settings)));
        settings.Rules[0].BlockDisplay = true;
        settings.Rules.Reverse();
        Assert.False(original.HasSameTranslationRules(new(settings)));
    }

    /// <summary>禁用的病态规则不执行，不增加超时状态，也不影响其他行。</summary>
    [Fact]
    public void DisabledPathologicalRulesDoNotRun()
    {
        var filter = new DanmuRegexFilter(new()
        {
            Rules = [new() { Pattern = "^(a+)+$" }, new() { Pattern = "^广告", BlockDisplay = true }]
        });
        var timedOut = new List<int>();
        filter.PatternTimedOut += timedOut.Add;
        var content = new string('a', 100_000) + "!";

        Assert.False(filter.IsBlocked(content));
        Assert.False(filter.IsTranslationBlocked(content));
        Assert.True(filter.IsBlocked("广告"));
        Assert.Equal(0, filter.StateVersion);
        Assert.Empty(timedOut);
    }

    /// <summary>超时号使用真实行号，翻译规则不在显示检查中执行，两个用途共享暂停状态。</summary>
    [Fact]
    public void TimedOutRuleKeepsActualIndexAndSharedState()
    {
        var settings = new DanmuRegexFilterSettings
        {
            Rules =
            [
                new() { Pattern = "^(b+)+$" },
                new() { Pattern = "^广告", BlockDisplay = true },
                new() { Pattern = "^(a+)+$", BlockTranslation = true },
                new() { Pattern = "!$", BlockTranslation = true }
            ]
        };
        var filter = new DanmuRegexFilter(settings);
        var timedOut = new List<int>();
        filter.PatternTimedOut += timedOut.Add;
        var content = new string('a', 100_000) + "!";

        Assert.False(filter.IsBlocked(content));
        Assert.Equal(0, filter.StateVersion);
        Assert.True(filter.IsTranslationBlocked(content));
        Assert.Equal(3, Assert.Single(timedOut));
        Assert.Equal(1, filter.StateVersion);
        Assert.False(filter.IsTranslationBlocked("aaa"));
        Assert.True(filter.IsBlocked("广告内容"));
        Assert.True(filter.IsTranslationBlocked(content));
        Assert.Single(timedOut);
        Assert.False(filter.HasSameTranslationRules(new(settings)));
        Assert.True(new DanmuRegexFilter(settings).IsTranslationBlocked("aaa"));
    }

    /// <summary>空结构和空白规则不能创建有效快照。</summary>
    [Fact]
    public void InvalidRuleStructuresAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(null!));
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(new() { Rules = null! }));
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(new() { Rules = [null!] }));
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(new() { Rules = [new() { Pattern = " " }] }));
        Assert.Throws<InvalidDataException>(() => new DanmuRegexFilter(new() { Rules = [new() { Pattern = null! }] }));
    }

    private static DanmuRegexFilterSettings DefaultSettings(bool blockDisplay)
    {
        var settings = new DanmuRegexFilterSettings();
        foreach (var rule in settings.Rules)
        {
            rule.BlockDisplay = blockDisplay;
        }
        return settings;
    }
}
