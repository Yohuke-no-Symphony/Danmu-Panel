using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>管理逐条正则的独立设置草稿、增删编号及保存前校验。</summary>
public sealed class DanmuRegexEditorViewModel : ObservableObject
{
    public ObservableCollection<DanmuRegexRuleViewModel> Rules { get; }
    public IRelayCommand AddRuleCommand { get; }
    public IRelayCommand<DanmuRegexRuleViewModel> RemoveRuleCommand { get; }

    /// <summary>复制初始规则列表，仅编辑草稿，不修改运行配置。</summary>
    public DanmuRegexEditorViewModel(DanmuRegexFilterSettings initial)
    {
        Rules = new(initial.Rules.Select(rule => new DanmuRegexRuleViewModel(rule)));
        AddRuleCommand = new RelayCommand(() => Rules.Add(new(new())));
        RemoveRuleCommand = new RelayCommand<DanmuRegexRuleViewModel>(RemoveRule,
            rule => rule is not null && Rules.Contains(rule));
        Rules.CollectionChanged += OnRulesChanged;
        UpdateNumbers();
    }

    /// <summary>验证所有行并生成独立配置；失败时返回第一条无效规则的一基序号。</summary>
    public bool TryCreateSettings(out DanmuRegexFilterSettings settings, out int invalidRuleNumber)
    {
        settings = null!;
        for (var index = 0; index < Rules.Count; index++)
        {
            if (!DanmuRegexFilter.IsValidPattern(Rules[index].Pattern))
            {
                invalidRuleNumber = index + 1;
                return false;
            }
        }
        settings = new() { Rules = Rules.Select(rule => rule.CreateSettings()).ToList() };
        invalidRuleNumber = 0;
        return true;
    }

    private void RemoveRule(DanmuRegexRuleViewModel? rule)
    {
        if (rule is not null)
        {
            Rules.Remove(rule);
        }
    }

    private void OnRulesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        UpdateNumbers();
        RemoveRuleCommand.NotifyCanExecuteChanged();
    }

    private void UpdateNumbers()
    {
        for (var index = 0; index < Rules.Count; index++)
        {
            Rules[index].Number = index + 1;
        }
    }
}
