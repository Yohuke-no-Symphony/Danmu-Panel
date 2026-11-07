using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>单条正则的独立编辑草稿，保留表达式原文及两个屏蔽选项。</summary>
public sealed class DanmuRegexRuleViewModel : ObservableObject
{
    private string pattern;
    private bool blockDisplay;
    private bool blockTranslation;
    private int number;

    public string Pattern
    {
        get => pattern;
        set => SetProperty(ref pattern, value);
    }

    public bool BlockDisplay
    {
        get => blockDisplay;
        set => SetProperty(ref blockDisplay, value);
    }

    public bool BlockTranslation
    {
        get => blockTranslation;
        set => SetProperty(ref blockTranslation, value);
    }

    public int Number
    {
        get => number;
        internal set => SetProperty(ref number, value);
    }

    /// <summary>复制持久化规则，不保留对原设置对象的可编辑引用。</summary>
    public DanmuRegexRuleViewModel(DanmuRegexRuleSettings initial)
    {
        pattern = initial.Pattern;
        blockDisplay = initial.BlockDisplay;
        blockTranslation = initial.BlockTranslation;
    }

    internal DanmuRegexRuleSettings CreateSettings()
    {
        return new() { Pattern = Pattern, BlockDisplay = BlockDisplay, BlockTranslation = BlockTranslation };
    }
}
