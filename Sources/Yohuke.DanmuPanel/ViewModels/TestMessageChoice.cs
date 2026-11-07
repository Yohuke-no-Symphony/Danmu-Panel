using CommunityToolkit.Mvvm.ComponentModel;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>随界面语言更新的测试消息选项。</summary>
public sealed class TestMessageChoice(string key) : ObservableObject
{
    public string Title => Localization.Get(key);

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
    }
}
