using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views.Settings;

/// <summary>在颜色行中呈现稳定的输入控件实例和本地化名称。</summary>
public sealed class ColorSettingRowPresentation(ColorSettingDefinition definition, ColorInput input) : ObservableObject
{
    public string Label => Localization.Get(definition.NameKey);
    public ColorInput Input { get; } = input;

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Label));
    }
}
