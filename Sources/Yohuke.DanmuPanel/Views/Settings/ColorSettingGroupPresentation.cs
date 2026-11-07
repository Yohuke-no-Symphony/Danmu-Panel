using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>承载颜色分组的固定输入实例及随语言更新的标题。</summary>
public sealed class ColorSettingGroupPresentation(string key, IReadOnlyList<ColorSettingRowPresentation> rows) : ObservableObject
{
    public string Name { get; } = key.Replace('.', '_');
    public string Title => Localization.Get(key);
    public IReadOnlyList<ColorSettingRowPresentation> Rows { get; } = rows;

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        foreach (var row in Rows)
        {
            row.RefreshLocalization();
        }
    }
}
