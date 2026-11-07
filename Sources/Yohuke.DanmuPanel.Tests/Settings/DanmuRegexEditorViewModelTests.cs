using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证逐条正则规则的独立草稿、增删编号及快照校验。</summary>
public sealed class DanmuRegexEditorViewModelTests
{
    /// <summary>初始规则、可编辑行和生成快照互相独立，表达式中的空格保持原样。</summary>
    [Fact]
    public void DraftAndSnapshotsOwnDetachedRules()
    {
        var initial = new DanmuRegexFilterSettings
        {
            Rules =
            [
                new() { Pattern = "^ 广告 $", BlockDisplay = true },
                new() { Pattern = "歌词", BlockTranslation = true }
            ]
        };
        var model = new DanmuRegexEditorViewModel(initial);
        model.Rules[0].Pattern = "^ 保留空格 $";
        model.Rules[0].BlockDisplay = false;
        model.Rules[0].BlockTranslation = true;

        Assert.Equal("^ 广告 $", initial.Rules[0].Pattern);
        Assert.True(initial.Rules[0].BlockDisplay);
        Assert.False(initial.Rules[0].BlockTranslation);
        Assert.True(model.TryCreateSettings(out var snapshot, out var invalidRuleNumber));
        Assert.Equal(0, invalidRuleNumber);
        Assert.Equal("^ 保留空格 $", snapshot.Rules[0].Pattern);
        Assert.False(snapshot.Rules[0].BlockDisplay);
        Assert.True(snapshot.Rules[0].BlockTranslation);
        Assert.NotSame(initial.Rules, snapshot.Rules);
        Assert.NotSame(initial.Rules[1], snapshot.Rules[1]);

        model.Rules[0].Pattern = "changed draft";
        snapshot.Rules[1].Pattern = "changed snapshot";
        snapshot.Rules[1].BlockDisplay = true;

        Assert.Equal("^ 保留空格 $", snapshot.Rules[0].Pattern);
        Assert.Equal("歌词", model.Rules[1].Pattern);
        Assert.False(model.Rules[1].BlockDisplay);
        Assert.Equal("歌词", initial.Rules[1].Pattern);
    }

    /// <summary>新增空规则默认关闭两个选项，删除或重新插入后按实际顺序编号。</summary>
    [Fact]
    public void AddingAndRemovingRulesRefreshesNumbersAndAllowsEmptyList()
    {
        var model = new DanmuRegexEditorViewModel(new() { Rules = [] });
        model.AddRuleCommand.Execute(null);
        model.AddRuleCommand.Execute(null);
        model.AddRuleCommand.Execute(null);
        var first = model.Rules[0];
        var last = model.Rules[2];

        Assert.Equal(new[] { 1, 2, 3 }, model.Rules.Select(rule => rule.Number));
        Assert.All(model.Rules, rule =>
        {
            Assert.Equal("", rule.Pattern);
            Assert.False(rule.BlockDisplay);
            Assert.False(rule.BlockTranslation);
        });
        Assert.True(model.RemoveRuleCommand.CanExecute(first));
        model.RemoveRuleCommand.Execute(first);
        Assert.False(model.RemoveRuleCommand.CanExecute(first));
        Assert.Equal(2, last.Number);
        Assert.Equal(new[] { 1, 2 }, model.Rules.Select(rule => rule.Number));
        model.Rules.Insert(0, first);
        Assert.Equal(new[] { 1, 2, 3 }, model.Rules.Select(rule => rule.Number));

        foreach (var rule in model.Rules.ToArray())
        {
            model.RemoveRuleCommand.Execute(rule);
        }

        Assert.True(model.TryCreateSettings(out var settings, out var invalidRuleNumber));
        Assert.Empty(settings.Rules);
        Assert.Equal(0, invalidRuleNumber);
    }

    /// <summary>空白、错误语法及真实换行即使双关也报告实际规则序号，不会静默丢弃。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[")]
    [InlineData("^a\nb$")]
    [InlineData("^a\rb$")]
    public void InvalidRulesAreRejectedAtTheirCurrentNumber(string pattern)
    {
        var model = new DanmuRegexEditorViewModel(new()
        {
            Rules = [new() { Pattern = "first" }, new() { Pattern = "second" }, new() { Pattern = pattern }]
        });

        Assert.False(model.TryCreateSettings(out _, out var invalidRuleNumber));
        Assert.Equal(3, invalidRuleNumber);
        Assert.Equal(3, model.Rules[2].Number);
        model.RemoveRuleCommand.Execute(model.Rules[0]);
        Assert.False(model.TryCreateSettings(out _, out invalidRuleNumber));
        Assert.Equal(2, invalidRuleNumber);
        Assert.Equal(pattern, model.Rules[1].Pattern);
        model.Rules[1].Pattern = @"^a\nb$";

        Assert.True(model.TryCreateSettings(out var settings, out invalidRuleNumber));
        Assert.Equal(0, invalidRuleNumber);
        Assert.Equal(@"^a\nb$", settings.Rules[1].Pattern);
    }

    /// <summary>每行开关单独保存，切换一条规则不会改变其他规则。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RulesKeepIndependentBlockingOptions(bool blockDisplay, bool blockTranslation)
    {
        var model = new DanmuRegexEditorViewModel(new()
        {
            Rules = [new() { Pattern = "same" }, new() { Pattern = "same", BlockTranslation = true }]
        });
        model.Rules[0].BlockDisplay = blockDisplay;
        model.Rules[0].BlockTranslation = blockTranslation;

        Assert.True(model.TryCreateSettings(out var settings, out _));
        Assert.Equal(blockDisplay, settings.Rules[0].BlockDisplay);
        Assert.Equal(blockTranslation, settings.Rules[0].BlockTranslation);
        Assert.False(settings.Rules[1].BlockDisplay);
        Assert.True(settings.Rules[1].BlockTranslation);
        Assert.Equal(2, settings.Rules.Count);
    }
}
