using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>为设置下拉框提供可本地化的数据项，由模板分别创建选中与弹出文本。</summary>
public sealed class SettingsChoiceOption : ObservableObject
{
    public string Key { get; }
    public string Text => Localization.Get(Key);

    /// <summary>保存选项的本地化键，不创建或持有界面控件。</summary>
    public SettingsChoiceOption(string key)
    {
        Key = key;
    }

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Text));
    }

    internal static IReadOnlyList<SettingsChoiceOption> Create(params string[] keys)
    {
        return keys.Select(key => new SettingsChoiceOption(key)).ToArray();
    }
}
